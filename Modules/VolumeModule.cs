using System;
using System.Management;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using DynamicIsland.Core;
using DynamicIsland.Shared;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// Громкость и яркость: крутишь звук — остров на секунду превращается в полоску.
    /// Плюс «Звук · AirPods Pro», когда меняется устройство вывода.
    /// </summary>
    public sealed class VolumeModule : IStartable, IDisposable, IMMNotificationClient
    {
        private static readonly Regex Headphones = new(@"airpods|buds|headphone|headset|наушник|гарнитур|beats|wh-|wf-", RegexOptions.IgnoreCase);

        private readonly IIslandHost _host;
        private readonly bool _volume;
        private readonly bool _brightness;
        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _device;
        private float _lastVolume = -1;
        private bool _lastMute;
        private string _deviceName = "";
        private string _lastDefaultId = "";
        private ManagementEventWatcher? _brightnessWatcher;
        private int _lastBrightness = -1;

        public VolumeModule(IIslandHost host, bool volume, bool brightness)
        {
            _host = host;
            _volume = volume;
            _brightness = brightness;
        }

        public void Start()
        {
            if (_volume)
            {
                _enumerator = new MMDeviceEnumerator();
                _enumerator.RegisterEndpointNotificationCallback(this);
                Attach(announce: false);
            }

            if (_brightness) StartBrightness();
        }

        // ------------------------------------------------------------ громкость

        private void Attach(bool announce)
        {
            try
            {
                if (_device != null)
                {
                    _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
                    _device.Dispose();
                    _device = null;
                }

                if (_enumerator == null || !_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return;

                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _deviceName = _device.FriendlyName;
                _lastDefaultId = _device.ID;
                _lastVolume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
                _lastMute = _device.AudioEndpointVolume.Mute;
                _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;

                if (announce)
                {
                    _host.ShowAlert(new IslandAlert
                    {
                        Key = "audio-device",
                        Glyph = Headphones.IsMatch(_deviceName) ? Glyphs.Headphones : Glyphs.Speaker,
                        Accent = Palette.Blue,
                        Title = "Звук",
                        Subtitle = _deviceName,
                        Duration = TimeSpan.FromSeconds(3),
                        DropIfHidden = true,
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Громкость: " + ex.Message);
            }
        }

        private void OnVolume(AudioVolumeNotificationData data)
        {
            float volume = data.MasterVolume;
            bool mute = data.Muted;
            if (Math.Abs(volume - _lastVolume) < 0.004f && mute == _lastMute) return;
            _lastVolume = volume;
            _lastMute = mute;

            int pct = (int)Math.Round(volume * 100);
            string glyph = mute || pct == 0 ? Glyphs.Mute
                : pct < 33 ? Glyphs.Volume1
                : pct < 66 ? Glyphs.Volume2
                : Glyphs.Volume3;

            _host.ShowAlert(new IslandAlert
            {
                Key = "osd",
                Glyph = glyph,
                Accent = mute ? Palette.Gray : Colors.White,
                Title = mute ? "Без звука" : $"Громкость · {pct}%",
                Subtitle = _deviceName,
                Progress = mute ? 0 : volume,
                Duration = TimeSpan.FromSeconds(1.6),
                Quiet = true,
                DropIfHidden = true,
            });
        }

        // ------------------------------------------------------------ IMMNotificationClient

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Render || role != Role.Multimedia) return;
            if (defaultDeviceId == _lastDefaultId) return;
            // COM-объекты пересоздаём на UI-потоке
            Application.Current?.Dispatcher.InvokeAsync(() => Attach(announce: true));
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        // ------------------------------------------------------------ яркость (ноутбуки)

        private void StartBrightness()
        {
            try
            {
                _brightnessWatcher = new ManagementEventWatcher(
                    new ManagementScope(@"root\WMI"),
                    new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                _brightnessWatcher.EventArrived += (_, e) =>
                {
                    try
                    {
                        int level = Convert.ToInt32(e.NewEvent.Properties["Brightness"].Value);
                        if (level == _lastBrightness) return;
                        _lastBrightness = level;

                        _host.ShowAlert(new IslandAlert
                        {
                            Key = "osd",
                            Glyph = Glyphs.Brightness,
                            Accent = Palette.Yellow,
                            Title = $"Яркость · {level}%",
                            Progress = level / 100.0,
                            Duration = TimeSpan.FromSeconds(1.6),
                            Quiet = true,
                            DropIfHidden = true,
                        });
                    }
                    catch { }
                };
                _brightnessWatcher.Start();
            }
            catch (Exception ex)
            {
                // у обычных мониторов ПК яркость через WMI не управляется — это нормально
                Log.Info("Яркость недоступна: " + ex.Message);
                _brightnessWatcher = null;
            }
        }

        public void Dispose()
        {
            try { _brightnessWatcher?.Stop(); _brightnessWatcher?.Dispose(); } catch { }
            try
            {
                if (_device != null) _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
                _device?.Dispose();
                _enumerator?.UnregisterEndpointNotificationCallback(this);
                _enumerator?.Dispose();
            }
            catch { }
        }
    }
}
