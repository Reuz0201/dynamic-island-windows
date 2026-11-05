using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Core;
using DynamicIsland.Shared;

namespace DynamicIsland
{
    /// <summary>
    /// «Полка»: перетащи файлы на остров — они полежат там, пока не вытащишь их
    /// в другую папку, чат или письмо. Сами файлы не копируются — хранится только путь.
    /// </summary>
    public partial class MainWindow
    {
        private const int ShelfLimit = 30;
        private const string ShelfActivityId = "shelf";

        private readonly List<string> _shelf = new();
        private IslandAlert? _dropAlert;
        private Point _chipDragStart;

        private static string ShelfFile => Path.Combine(IslandSettings.Folder, "shelf.json");

        private void InitShelf()
        {
            LoadShelf();

            IslandRow.DragEnter += OnShelfDragEnter;
            IslandRow.DragOver += OnShelfDragOver;
            IslandRow.DragLeave += OnShelfDragLeave;
            IslandRow.Drop += OnShelfDrop;

            UpdateShelf();
        }

        // ------------------------------------------------------------ бросили файлы на остров

        private bool CanAcceptDrop(DragEventArgs e) =>
            _settings.ModuleShelf && e.Data.GetDataPresent(DataFormats.FileDrop);

        private void OnShelfDragEnter(object sender, DragEventArgs e)
        {
            if (!CanAcceptDrop(e)) return;
            e.Effects = DragDropEffects.Link;
            e.Handled = true;

            if (_dropAlert != null) return;
            int count = (e.Data.GetData(DataFormats.FileDrop) as string[])?.Length ?? 1;
            _dropAlert = new IslandAlert
            {
                Key = "shelf-drop",
                Glyph = Glyphs.Attach,
                Accent = Palette.Yellow,
                Title = "Отпусти — положу на полку",
                Subtitle = count == 1 ? "1 файл" : $"{count} файлов",
                Duration = null,
            };
            ShowAlert(_dropAlert);
        }

        private void OnShelfDragOver(object sender, DragEventArgs e)
        {
            if (!CanAcceptDrop(e))
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
        }

        private void OnShelfDragLeave(object sender, DragEventArgs e)
        {
            // DragLeave прилетает и при переходе между дочерними элементами — проверяем, что реально ушли
            var p = e.GetPosition(IslandRow);
            if (p.X >= 0 && p.Y >= 0 && p.X <= IslandRow.ActualWidth && p.Y <= IslandRow.ActualHeight) return;
            CloseDropAlert();
        }

        private void OnShelfDrop(object sender, DragEventArgs e)
        {
            CloseDropAlert();
            if (!CanAcceptDrop(e) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            e.Handled = true;

            int added = 0;
            foreach (var f in files)
            {
                if (_shelf.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
                _shelf.Add(f);
                added++;
            }
            while (_shelf.Count > ShelfLimit) _shelf.RemoveAt(0);

            SaveShelf();
            UpdateShelf();
            Log.Info($"На полку: {added} файл(ов)");

            ShowAlert(new IslandAlert
            {
                Key = "shelf-added",
                Glyph = Glyphs.Attach,
                Accent = Palette.Yellow,
                Title = "На полке",
                Subtitle = files.Length == 1 ? Path.GetFileName(files[0]) : $"+{added} · всего {_shelf.Count}",
                Detail = "наведи на остров и перетащи файл, куда нужно",
                Duration = TimeSpan.FromSeconds(2.5),
            });
        }

        private void CloseDropAlert()
        {
            if (_dropAlert == null) return;
            var a = _dropAlert;
            _dropAlert = null;
            DismissAlert(a);
        }

        // ------------------------------------------------------------ интерфейс полки

        private void UpdateShelf()
        {
            _shelf.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));

            ShelfPanel.Children.Clear();

            if (!_settings.ModuleShelf || _shelf.Count == 0)
            {
                ShelfPanel.Visibility = Visibility.Collapsed;
                RemoveActivity(ShelfActivityId);
                return;
            }

            ShelfPanel.Visibility = Visibility.Visible;
            foreach (var path in _shelf) ShelfPanel.Children.Add(BuildChip(path));

            int n = _shelf.Count;
            SetActivity(new LiveActivity(ShelfActivityId)
            {
                Order = 40,
                Glyph = Glyphs.Attach,
                Accent = Palette.Yellow,
                Title = "Полка",
                Subtitle = $"{n} {Plural(n, "файл", "файла", "файлов")} · тащи, куда нужно",
                Badge = n.ToString(),
                Actions =
                {
                    new IslandAction { Glyph = Glyphs.Close, Tooltip = "Очистить полку", OnClick = ClearShelf },
                },
            });
        }

        private void ClearShelf()
        {
            _shelf.Clear();
            SaveShelf();
            UpdateShelf();
        }

        private static string Plural(int n, string one, string few, string many)
        {
            n = Math.Abs(n) % 100;
            int n1 = n % 10;
            if (n > 10 && n < 20) return many;
            if (n1 > 1 && n1 < 5) return few;
            return n1 == 1 ? one : many;
        }

        private FrameworkElement BuildChip(string path)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var icon = FileIcon(path);
            if (icon != null)
                panel.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });

            panel.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(path.TrimEnd('\\')),
                FontSize = 12,
                Foreground = Brushes.White,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var remove = Views.IconButton(Glyphs.Close, "Убрать с полки", 20, () =>
            {
                _shelf.Remove(path);
                SaveShelf();
                UpdateShelf();
            });
            remove.Margin = new Thickness(4, 0, -4, 0);
            panel.Children.Add(remove);

            var chip = new Border
            {
                Child = panel,
                Background = Views.Brush(Colors.White, 0x1A),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 6, 6, 0),
                Cursor = Cursors.Hand,
                ToolTip = path,
            };

            // вытаскиваем файл: тянем фишку с полки куда угодно (Проводник, Telegram, браузер)
            chip.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _chipDragStart = e.GetPosition(null);
                if (e.ClickCount == 2) OpenPath(path);
            };
            chip.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var pos = e.GetPosition(null);
                if (Math.Abs(pos.X - _chipDragStart.X) < 6 && Math.Abs(pos.Y - _chipDragStart.Y) < 6) return;

                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                var result = DragDrop.DoDragDrop(chip, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);

                // файл перенесли (а не скопировали) — на полке ему больше не место
                if (result == DragDropEffects.Move && !File.Exists(path))
                {
                    _shelf.Remove(path);
                    SaveShelf();
                    UpdateShelf();
                }
            };

            return chip;
        }

        private static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        // ------------------------------------------------------------ хранение

        private void LoadShelf()
        {
            try
            {
                if (File.Exists(ShelfFile))
                    _shelf.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ShelfFile)) ?? new List<string>());
            }
            catch (Exception ex)
            {
                Log.Warn("Полка не загрузилась: " + ex.Message);
            }
        }

        private void SaveShelf()
        {
            try
            {
                Directory.CreateDirectory(IslandSettings.Folder);
                File.WriteAllText(ShelfFile, JsonSerializer.Serialize(_shelf));
            }
            catch { }
        }

        // ------------------------------------------------------------ иконки файлов

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1;

        private static ImageSource? FileIcon(string path)
        {
            try
            {
                var info = new SHFILEINFO();
                SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON);
                if (info.hIcon == IntPtr.Zero) return null;
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
