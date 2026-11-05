using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Core;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// VPN подключился/отключился + куда: страна, город, IP.
    /// Ловит VPN-адаптеры (WireGuard, Amnezia, OpenVPN, Outline, встроенный VPN Windows...)
    /// и просто смену внешнего IP — так виден даже VPN в режиме прокси.
    /// </summary>
    public sealed class VpnModule : IStartable, IDisposable
    {
        private static readonly string[] VpnKeywords =
        {
            "vpn", "kitten", "wireguard", "wintun", "wg", "amnezia", "openvpn", "tap-windows", "tap0901", "tun",
            "outline", "proton", "nord", "windscribe", "mullvad", "surfshark", "expressvpn", "hiddify",
            "v2ray", "xray", "sing-box", "singbox", "clash", "nekobox", "nekoray", "warp", "cloudflare",
            "zerotier", "tailscale", "softether", "fortinet", "cisco anyconnect", "pangp", "adguard",
        };

        // это LAN-штуки, не «впн наружу»
        private static readonly string[] NotVpn = { "высокоскоростн", "pppoe", "broadband", "radmin", "hamachi", "virtualbox", "vmware", "hyper-v", "vethernet", "bluetooth" };

        private sealed record GeoInfo(string Ip, string Country, string CountryCode, string City, string Isp)
        {
            public string Place => string.IsNullOrEmpty(City) || City == Country ? Country : $"{Country}, {City}";
        }

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(7) };

        private readonly IIslandHost _host;
        private readonly SemaphoreSlim _checkLock = new(1, 1);
        private Timer? _pollTimer;
        private Timer? _debounce;

        private HashSet<string> _vpnAdapters = new();
        private Dictionary<string, string> _adapterNames = new();
        private GeoInfo? _geo;
        private bool _initialized;
        private volatile bool _again;

        public VpnModule(IIslandHost host) => _host = host;

        public void Start()
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;

            _ = CheckAsync(initial: true);

            // VPN в режиме прокси адаптер не создаёт — раз в 90 сек сверяем внешний IP
            _pollTimer = new Timer(_ => _ = CheckAsync(initial: false), null, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(90));
        }

        private void OnNetworkChanged(object? sender, EventArgs e) => Debounce();
        private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Debounce();

        private void Debounce()
        {
            // при подключении VPN событий прилетает пачка — ждём, пока всё уляжется
            _debounce?.Dispose();
            _debounce = new Timer(_ => _ = CheckAsync(initial: false), null, 2500, Timeout.Infinite);
        }

        public static bool LooksLikeVpn(NetworkInterface nic)
        {
            string text = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (NotVpn.Any(text.Contains)) return false;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Ppp) return true; // встроенный VPN Windows
            return ContainsVpnWord(text);
        }

        /// <summary>Имя процесса похоже на VPN-клиент (kittend, wireguard, sing-box...).</summary>
        public static bool LooksLikeVpnName(string processName)
        {
            string text = processName.ToLowerInvariant();
            if (NotVpn.Any(text.Contains)) return false;
            return ContainsVpnWord(text);
        }

        private static bool ContainsVpnWord(string text)
        {
            foreach (var word in VpnKeywords)
            {
                // короткие слова (wg, tun) — только как отдельное слово, иначе «tun» найдётся где попало
                if (word.Length <= 3)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(text, $@"(^|[^a-z]){word}($|[^a-z]|\d)"))
                        return true;
                }
                else if (text.Contains(word))
                {
                    return true;
                }
            }
            return false;
        }

        private static Dictionary<string, string> ActiveVpnAdapters()
        {
            var result = new Dictionary<string, string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (!LooksLikeVpn(nic)) continue;
                result[nic.Id] = PrettyName(nic);
            }
            return result;
        }

        private static string PrettyName(NetworkInterface nic)
        {
            string name = nic.Name;
            string desc = nic.Description;
            // имя адаптера обычно понятнее («AmneziaVPN»), но «Подключение по локальной сети 3» — нет
            if (name.StartsWith("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Подключение", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Local Area", StringComparison.OrdinalIgnoreCase))
                return desc;
            return name;
        }

        private async Task CheckAsync(bool initial)
        {
            if (!await _checkLock.WaitAsync(0))
            {
                // проверка уже идёт — попросим её повториться в конце
                _again = true;
                return;
            }
            try
            {
                var adapters = ActiveVpnAdapters();
                var ids = adapters.Keys.ToHashSet();

                var connected = ids.Except(_vpnAdapters).ToList();
                var disconnected = _vpnAdapters.Except(ids).ToList();
                var oldNames = _adapterNames;

                // даём маршрутам устояться, прежде чем спрашивать IP
                if (!initial && (connected.Count > 0 || disconnected.Count > 0))
                    await Task.Delay(1500);

                var geo = await FetchGeoAsync();

                bool ipChanged = geo != null && _geo != null && geo.Ip != _geo.Ip;
                bool countryChanged = geo != null && _geo != null && geo.CountryCode != _geo.CountryCode;

                _vpnAdapters = ids;
                _adapterNames = adapters;
                var previousGeo = _geo;
                if (geo != null) _geo = geo;

                UpdateStatus();

                if (initial || !_initialized)
                {
                    _initialized = true;
                    return;
                }

                if (connected.Count > 0)
                {
                    string via = string.Join(", ", connected.Select(id => adapters[id]));
                    ShowGeo("VPN подключён", $"через {via}", geo, Palette.Green, Glyphs.Shield);
                }
                else if (disconnected.Count > 0)
                {
                    string via = string.Join(", ", disconnected.Select(id => oldNames.TryGetValue(id, out var n) ? n : "VPN"));
                    ShowGeo("VPN отключён", via, geo, Palette.Orange, Glyphs.Shield);
                }
                else if (countryChanged)
                {
                    // адаптеры не менялись, а страна сменилась — VPN-прокси или смена сервера
                    ShowGeo("Сменилась страна", previousGeo != null ? $"было: {previousGeo.Place}" : "", geo, Palette.Cyan, Glyphs.Globe);
                }
                else if (ipChanged && _vpnAdapters.Count > 0)
                {
                    ShowGeo("Сменился IP", "VPN переподключился", geo, Palette.Cyan, Glyphs.Globe);
                }
            }
            catch
            {
                // нет сети — попробуем в следующий раз
            }
            finally
            {
                _checkLock.Release();
            }

            if (_again)
            {
                _again = false;
                await CheckAsync(initial: false);
            }
        }

        private void UpdateStatus()
        {
            if (_vpnAdapters.Count > 0)
            {
                string where = _geo != null ? _geo.Place : "подключён";
                _host.SetStatus("vpn", Glyphs.Shield, Palette.Green, $"VPN · {where}");
            }
            else
            {
                _host.SetStatus("vpn", null, Palette.Green, "");
            }
        }

        private void ShowGeo(string title, string detail, GeoInfo? geo, Color accent, string glyph)
        {
            string subtitle = geo != null ? geo.Place : "не удалось узнать страну";

            string? detailLine = geo == null ? null
                : string.IsNullOrEmpty(geo.Isp) ? geo.Ip : $"{geo.Ip} · {geo.Isp}";
            if (!string.IsNullOrEmpty(detail))
                detailLine = detailLine == null ? detail : $"{detail} · {detailLine}";

            string cc = geo?.CountryCode?.ToLowerInvariant() ?? "";

            _host.ShowAlert(new IslandAlert
            {
                Key = "vpn",
                Glyph = glyph,
                Accent = accent,
                Title = title,
                Subtitle = subtitle,
                Detail = detailLine,
                IconFactory = cc.Length == 2 ? () => FlagIcon(cc, glyph, accent) : null,
                Duration = TimeSpan.FromSeconds(6),
            });
        }

        /// <summary>Флаг страны + маленький значок (щит/глобус) в углу.</summary>
        private static FrameworkElement FlagIcon(string countryCode, string glyph, Color accent)
        {
            var grid = new Grid { Width = 44, Height = 40, VerticalAlignment = VerticalAlignment.Center };

            var flag = Views.RemoteImage($"https://flagcdn.com/w80/{countryCode}.png", 40, 28, 6, glyph, accent);
            flag.HorizontalAlignment = HorizontalAlignment.Left;
            flag.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(flag);

            var badge = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = Views.Brush(accent),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = Views.Glyph(glyph, 9, Brushes.Black),
            };
            grid.Children.Add(badge);
            return grid;
        }

        private static async Task<GeoInfo?> FetchGeoAsync()
        {
            // ip-api.com: бесплатно, без ключа, умеет по-русски (только http)
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                    "http://ip-api.com/json/?fields=status,country,countryCode,city,isp,query&lang=ru"));
                var r = doc.RootElement;
                if (r.GetProperty("status").GetString() == "success")
                {
                    return new GeoInfo(
                        r.GetProperty("query").GetString() ?? "",
                        r.GetProperty("country").GetString() ?? "",
                        r.GetProperty("countryCode").GetString() ?? "",
                        r.GetProperty("city").GetString() ?? "",
                        r.GetProperty("isp").GetString() ?? "");
                }
            }
            catch { }

            // запасной: ipwho.is (https)
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/?lang=ru"));
                var r = doc.RootElement;
                if (r.GetProperty("success").GetBoolean())
                {
                    string isp = r.TryGetProperty("connection", out var c) && c.TryGetProperty("isp", out var i)
                        ? i.GetString() ?? "" : "";
                    return new GeoInfo(
                        r.GetProperty("ip").GetString() ?? "",
                        r.GetProperty("country").GetString() ?? "",
                        r.GetProperty("country_code").GetString() ?? "",
                        r.GetProperty("city").GetString() ?? "",
                        isp);
                }
            }
            catch { }

            return null;
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
            _pollTimer?.Dispose();
            _debounce?.Dispose();
        }
    }
}
