﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Navigation;
using Unigram.Services.Settings;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;

namespace Compatibility
{
    public static class MissingFrameworkFunctions
    {
        /// <summary>
        /// Creates a relative path from one file or folder to another.
        /// </summary>
        /// <param name="fromPath">Contains the directory that defines the start of the relative path.</param>
        /// <param name="toPath">Contains the path that defines the endpoint of the relative path.</param>
        /// <returns>The relative path from the start directory to the end path.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="fromPath"/> or <paramref name="toPath"/> is <c>null</c>.</exception>
        /// <exception cref="UriFormatException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        public static string GetRelativePath(string fromPath, string toPath)
        {
            if (string.IsNullOrEmpty(fromPath))
            {
                throw new ArgumentNullException("fromPath");
            }

            if (string.IsNullOrEmpty(toPath))
            {
                throw new ArgumentNullException("toPath");
            }

            Uri fromUri = new Uri(AppendDirectorySeparatorChar(fromPath));
            Uri toUri = new Uri(AppendDirectorySeparatorChar(toPath));

            if (fromUri.Scheme != toUri.Scheme)
            {
                return toPath;
            }

            Uri relativeUri = fromUri.MakeRelativeUri(toUri);
            string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            if (string.Equals(toUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }

            return relativePath;
        }

        private static string AppendDirectorySeparatorChar(string path)
        {
            // Append a slash only if the path is a directory and does not have a slash.
            if (!Path.HasExtension(path) &&
                !path.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                return path + Path.DirectorySeparatorChar;
            }

            return path;
        }
    }
}

namespace Unigram.Services
{
    public interface IThemeService
    {
        Dictionary<string, string[]> GetMapping(TelegramTheme flags);
        Color GetDefaultColor(TelegramTheme flags, string key);

        Task<IList<ThemeInfoBase>> GetThemesAsync(bool custom);

        Task SerializeAsync(StorageFile file, ThemeCustomInfo theme);
        Task<ThemeCustomInfo> DeserializeAsync(StorageFile file);

        Task InstallThemeAsync(StorageFile file);
        Task SetThemeAsync(ThemeInfoBase info);

        void Refresh();
    }

    public class ThemeService : IThemeService
    {
        private readonly IProtoService _protoService;
        private readonly ISettingsService _settingsService;
        private readonly IEventAggregator _aggregator;

        private readonly UISettings _uiSettings;

        public ThemeService(IProtoService protoService, ISettingsService settingsService, IEventAggregator aggregator)
        {
            _protoService = protoService;
            _settingsService = settingsService;
            _aggregator = aggregator;

            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        }

        private void OnColorValuesChanged(UISettings sender, object args)
        {
            _aggregator.Publish(new UpdateSelectedBackground(true, _protoService.GetSelectedBackground(true)));
            _aggregator.Publish(new UpdateSelectedBackground(false, _protoService.GetSelectedBackground(false)));
        }

        public Dictionary<string, string[]> GetMapping(TelegramTheme flags)
        {
            return flags.HasFlag(TelegramTheme.Dark) ? _mappingDark : _mapping;
        }

        public Color GetDefaultColor(TelegramTheme flags, string key)
        {
            var resources = flags.HasFlag(TelegramTheme.Dark) ? _defaultDark : _defaultLight;

            while (resources.TryGetValue(key, out object value))
            {
                if (value is string)
                {
                    key = value as string;
                }
                else if (value is Color color)
                {
                    return color;
                }
            }

            return default(Color);
        }

        public async Task<IList<ThemeInfoBase>> GetThemesAsync(bool custom)
        {
            var result = new List<ThemeInfoBase>();

            if (custom)
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("themes", CreationCollisionOption.OpenIfExists);
                var files = await folder.GetFilesAsync();

                foreach (var file in files)
                {
                    result.Add(await DeserializeAsync(file, false));
                }
            }
            else
            {
                result.Add(new ThemeBundledInfo { Name = Strings.Additional.ThemeLight, Parent = TelegramTheme.Light });
                result.Add(new ThemeBundledInfo { Name = Locale.GetString("ThemeDark"), Parent = TelegramTheme.Dark });

                var package = await Package.Current.InstalledLocation.GetFolderAsync("Assets\\Themes");
                var official = await package.GetFilesAsync();

                foreach (var file in official)
                {
                    result.Add(await DeserializeAsync(file, true));
                }

                result.Add(new ThemeSystemInfo { Name = Strings.Additional.ThemeSystemTheme });
            }

            return result;
        }

        public async Task SerializeAsync(StorageFile file, ThemeCustomInfo theme)
        {
            var lines = new StringBuilder();
            lines.AppendLine("!");
            lines.AppendLine($"name: {theme.Name}");
            lines.AppendLine($"parent: {(int)theme.Parent}");

            var lastbrush = false;

            foreach (var item in theme.Values)
            {
                if (item.Value is Color color)
                {
                    if (!lastbrush)
                    {
                        lines.AppendLine("#");
                    }

                    var hexValue = (color.A << 24) + (color.R << 16) + (color.G << 8) + (color.B & 0xff);

                    lastbrush = true;
                    lines.AppendLine(string.Format("{0}: #{1:X8}", item.Key, hexValue));
                }
            }

            await FileIO.WriteTextAsync(file, lines.ToString());
        }

        public Task<ThemeCustomInfo> DeserializeAsync(StorageFile file)
        {
            return DeserializeAsync(file, false);
        }

        private async Task<ThemeCustomInfo> DeserializeAsync(StorageFile file, bool official)
        {
            var lines = await FileIO.ReadLinesAsync(file);
            var theme = new ThemeCustomInfo(official);

            if (official)
            {
                theme.Path = Compatibility.MissingFrameworkFunctions.GetRelativePath(Package.Current.InstalledLocation.Path, file.Path);
                
            }
            else
            {
                theme.Path = file.Path;
            }

            foreach (var line in lines)
            {
                if (line.StartsWith("name: "))
                {
                    theme.Name = line.Substring("name: ".Length);
                }
                else if (line.StartsWith("parent: "))
                {
                    theme.Parent = (TelegramTheme)int.Parse(line.Substring("parent: ".Length));
                }
                else if (line.Equals("!") || line.Equals("#"))
                {
                    continue;
                }
                else
                {
                    var split = line.Split(':');
                    var key = split[0].Trim();
                    var value = split[1].Trim();

                    if (value.StartsWith("#") && int.TryParse(value.Substring(1), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hexValue))
                    {
                        byte a = (byte)((hexValue & 0xff000000) >> 24);
                        byte r = (byte)((hexValue & 0x00ff0000) >> 16);
                        byte g = (byte)((hexValue & 0x0000ff00) >> 8);
                        byte b = (byte)(hexValue & 0x000000ff);

                        theme.Values[key] = Color.FromArgb(a, r, g, b);
                    }
                }
            }

            return theme;
        }



        public async Task InstallThemeAsync(StorageFile file)
        {
            var info = await DeserializeAsync(file);
            if (info == null)
            {
                return;
            }

            var installed = await GetThemesAsync(true);

            var equals = installed.FirstOrDefault(x => x is ThemeCustomInfo custom && ThemeCustomInfo.Equals(custom, info));
            if (equals != null)
            {
                await SetThemeAsync(equals);
                return;
            }

            var folder = await ApplicationData.Current.LocalFolder.GetFolderAsync("themes");
            var result = await file.CopyAsync(folder, file.Name, NameCollisionOption.GenerateUniqueName);

            var theme = await DeserializeAsync(result);
            if (theme != null)
            {
                await SetThemeAsync(theme);
            }
        }

        public async Task SetThemeAsync(ThemeInfoBase info)
        {
            if (info is ThemeSystemInfo)
            {
                _settingsService.Appearance.RequestedTheme = ElementTheme.Default;
            }
            else
            {
                _settingsService.Appearance.RequestedTheme = info.Parent.HasFlag(TelegramTheme.Light) ? ElementTheme.Light : ElementTheme.Dark;
            }

            if (info is ThemeCustomInfo custom)
            {
                _settingsService.Appearance.RequestedThemePath = custom.Path;
            }
            else
            {
                _settingsService.Appearance.RequestedThemePath = null;
            }

            var flags = _settingsService.Appearance.GetCalculatedElementTheme();

            foreach (TLWindowContext window in WindowContext.ActiveWrappers)
            {
                await window.Dispatcher.DispatchAsync(() =>
                {
                    Theme.Current.Update(info as ThemeCustomInfo);

                    window.UpdateTitleBar();

                    if (window.Content is FrameworkElement element)
                    {
                        if (flags == element.RequestedTheme)
                        {
                            element.RequestedTheme = flags == ElementTheme.Dark
                                ? ElementTheme.Light
                                : ElementTheme.Dark;
                        }

                        element.RequestedTheme = flags;
                    }
                });
            }

            _aggregator.Publish(new UpdateSelectedBackground(true, _protoService.GetSelectedBackground(true)));
            _aggregator.Publish(new UpdateSelectedBackground(false, _protoService.GetSelectedBackground(false)));
        }

        public async void Refresh()
        {
            var flags = _settingsService.Appearance.RequestedTheme;

            foreach (TLWindowContext window in WindowContext.ActiveWrappers)
            {
                await window.Dispatcher.DispatchAsync(() =>
                {
                    if (window.Content is FrameworkElement element)
                    {
                        if (flags == element.RequestedTheme)
                        {
                            element.RequestedTheme = flags == ElementTheme.Dark
                                ? ElementTheme.Light
                                : ElementTheme.Dark;
                        }

                        element.RequestedTheme = flags;
                    }
                });
            }
        }



        private readonly Dictionary<string, string[]> _mapping = new Dictionary<string, string[]>
        {
            { "SystemControlPageTextBaseMediumBrush", new[] { "SystemControlDescriptionTextForegroundBrush", "HyperlinkButtonForegroundPointerOver", "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver" } },
            { "SystemControlTransparentBrush", new[] { "SliderContainerBackground", "SliderContainerBackgroundPointerOver", "SliderContainerBackgroundPressed", "SliderContainerBackgroundDisabled", "RadioButtonBackground", "RadioButtonBackgroundPointerOver", "RadioButtonBackgroundPressed", "RadioButtonBackgroundDisabled", "RadioButtonBorderBrush", "RadioButtonBorderBrushPointerOver", "RadioButtonBorderBrushPressed", "RadioButtonBorderBrushDisabled", "RadioButtonOuterEllipseFill", "RadioButtonOuterEllipseFillPointerOver", "RadioButtonOuterEllipseFillPressed", "RadioButtonOuterEllipseFillDisabled", "RadioButtonOuterEllipseCheckedFillDisabled", "RadioButtonCheckGlyphStroke", "RadioButtonCheckGlyphStrokePointerOver", "RadioButtonCheckGlyphStrokePressed", "RadioButtonCheckGlyphStrokeDisabled", "CheckBoxBackgroundUnchecked", "CheckBoxBackgroundUncheckedPointerOver", "CheckBoxBackgroundUncheckedPressed", "CheckBoxBackgroundUncheckedDisabled", "CheckBoxBackgroundChecked", "CheckBoxBackgroundCheckedPointerOver", "CheckBoxBackgroundCheckedPressed", "CheckBoxBackgroundCheckedDisabled", "CheckBoxBackgroundIndeterminate", "CheckBoxBackgroundIndeterminatePointerOver", "CheckBoxBackgroundIndeterminatePressed", "CheckBoxBackgroundIndeterminateDisabled", "CheckBoxBorderBrushUnchecked", "CheckBoxBorderBrushUncheckedPointerOver", "CheckBoxBorderBrushUncheckedPressed", "CheckBoxBorderBrushUncheckedDisabled", "CheckBoxBorderBrushChecked", "CheckBoxBorderBrushCheckedPointerOver", "CheckBoxBorderBrushCheckedPressed", "CheckBoxBorderBrushCheckedDisabled", "CheckBoxBorderBrushIndeterminate", "CheckBoxBorderBrushIndeterminatePointerOver", "CheckBoxBorderBrushIndeterminatePressed", "CheckBoxBorderBrushIndeterminateDisabled", "CheckBoxCheckBackgroundFillUnchecked", "CheckBoxCheckBackgroundFillUncheckedPointerOver", "CheckBoxCheckBackgroundFillUncheckedDisabled", "CheckBoxCheckBackgroundFillCheckedDisabled", "CheckBoxCheckBackgroundFillIndeterminateDisabled", "HyperlinkButtonBorderBrush", "HyperlinkButtonBorderBrushPointerOver", "HyperlinkButtonBorderBrushPressed", "HyperlinkButtonBorderBrushDisabled", "ToggleSwitchContainerBackground", "ToggleSwitchContainerBackgroundPointerOver", "ToggleSwitchContainerBackgroundPressed", "ToggleSwitchContainerBackgroundDisabled", "ToggleSwitchFillOff", "ToggleSwitchFillOffPointerOver", "ToggleSwitchFillOffDisabled", "ToggleButtonBorderBrushCheckedPressed", "ScrollBarBackground", "ScrollBarBackgroundPointerOver", "ScrollBarBackgroundDisabled", "ScrollBarForeground", "ScrollBarBorderBrush", "ScrollBarBorderBrushPointerOver", "ScrollBarBorderBrushDisabled", "ScrollBarButtonBackground", "ScrollBarButtonBackgroundDisabled", "ScrollBarButtonBorderBrush", "ScrollBarButtonBorderBrushPointerOver", "ScrollBarButtonBorderBrushPressed", "ScrollBarButtonBorderBrushDisabled", "ListViewHeaderItemBackground", "ComboBoxItemBackground", "ComboBoxItemBackgroundDisabled", "ComboBoxItemBackgroundSelectedDisabled", "ComboBoxItemBorderBrush", "ComboBoxItemBorderBrushPressed", "ComboBoxItemBorderBrushPointerOver", "ComboBoxItemBorderBrushDisabled", "ComboBoxItemBorderBrushSelected", "ComboBoxItemBorderBrushSelectedUnfocused", "ComboBoxItemBorderBrushSelectedPressed", "ComboBoxItemBorderBrushSelectedPointerOver", "ComboBoxItemBorderBrushSelectedDisabled", "AppBarEllipsisButtonBackground", "AppBarEllipsisButtonBackgroundDisabled", "AppBarEllipsisButtonBorderBrush", "AppBarEllipsisButtonBorderBrushPointerOver", "AppBarEllipsisButtonBorderBrushPressed", "AppBarEllipsisButtonBorderBrushDisabled", "CalendarViewNavigationButtonBackground", "CalendarViewNavigationButtonBorderBrush", "FlipViewItemBackground", "DateTimePickerFlyoutButtonBackground", "TextControlButtonBackground", "TextControlButtonBackgroundPointerOver", "TextControlButtonBorderBrush", "TextControlButtonBorderBrushPointerOver", "TextControlButtonBorderBrushPressed", "ToggleMenuFlyoutItemBackground", "ToggleMenuFlyoutItemBackgroundDisabled", "PivotBackground", "PivotHeaderBackground", "PivotItemBackground", "PivotHeaderItemBackgroundUnselected", "PivotHeaderItemBackgroundDisabled", "GridViewHeaderItemBackground", "GridViewItemBackground", "GridViewItemDragBackground", "MenuFlyoutItemBackground", "MenuFlyoutItemBackgroundDisabled", "MenuFlyoutSubItemBackground", "MenuFlyoutSubItemBackgroundDisabled", "NavigationViewItemBorderBrushDisabled", "NavigationViewItemBorderBrushCheckedDisabled", "NavigationViewItemBorderBrushSelectedDisabled", "TopNavigationViewItemBackgroundPointerOver", "TopNavigationViewItemBackgroundPressed", "TopNavigationViewItemBackgroundSelected", "NavigationViewBackButtonBackground", "MenuBarBackground", "MenuBarItemBackground", "AppBarButtonBackground", "AppBarButtonBackgroundDisabled", "AppBarButtonBorderBrush", "AppBarButtonBorderBrushPointerOver", "AppBarButtonBorderBrushPressed", "AppBarButtonBorderBrushDisabled", "AppBarToggleButtonBackground", "AppBarToggleButtonBackgroundDisabled", "AppBarToggleButtonBackgroundHighLightOverlay", "AppBarToggleButtonBorderBrush", "AppBarToggleButtonBorderBrushPointerOver", "AppBarToggleButtonBorderBrushPressed", "AppBarToggleButtonBorderBrushDisabled", "AppBarToggleButtonBorderBrushChecked", "AppBarToggleButtonBorderBrushCheckedPointerOver", "AppBarToggleButtonBorderBrushCheckedPressed", "AppBarToggleButtonBorderBrushCheckedDisabled", "ListViewItemBackground", "ListViewItemDragBackground", "TreeViewItemBackgroundDisabled", "TreeViewItemBorderBrush", "TreeViewItemBorderBrushDisabled", "TreeViewItemBorderBrushSelected", "TreeViewItemBorderBrushSelectedDisabled", "TreeViewItemCheckBoxBackgroundSelected", "CommandBarFlyoutButtonBackground", "AppBarButtonBorderBrushSubMenuOpened" } },
            { "SystemControlForegroundAccentBrush", new[] { "SliderThumbBackground", "CheckBoxCheckBackgroundStrokeIndeterminate", "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "RatingControlSelectedForeground", "RatingControlPointerOverSelectedForeground", "NavigationViewSelectionIndicatorForeground" } },
            { "SystemControlHighlightChromeAltLowBrush", new[] { "SliderThumbBackgroundPointerOver", "ColorPickerSliderThumbBackgroundPointerOver" } },
            { "SystemControlHighlightChromeHighBrush", new[] { "SliderThumbBackgroundPressed" } },
            { "SystemControlDisabledChromeDisabledHighBrush", new[] { "SliderThumbBackgroundDisabled", "SliderTrackFillDisabled", "SliderTrackValueFillDisabled", "GridViewItemPlaceholderBackground", "ColorPickerSliderThumbBackgroundDisabled", "ListViewItemPlaceholderBackground" } },
            { "SystemControlForegroundBaseMediumLowBrush", new[] { "SliderTrackFill", "SliderTrackFillPressed", "SliderTickBarFill", "ComboBoxBorderBrush", "AppBarSeparatorForeground", "CalendarDatePickerBorderBrush", "DatePickerButtonBorderBrush", "TimePickerButtonBorderBrush", "TextControlBorderBrush", "MenuBarItemBorderBrush" } },
            { "SystemControlForegroundBaseMediumBrush", new[] { "SliderTrackFillPointerOver", "CheckBoxCheckGlyphForegroundIndeterminatePressed", "CalendarDatePickerTextForeground", "CalendarViewNavigationButtonForegroundPressed", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForeground", "PivotHeaderItemForegroundUnselected", "RatingControlPointerOverPlaceholderForeground", "RatingControlPointerOverUnselectedForeground", "RatingControlCaptionForeground", "TopNavigationViewItemForeground", "MenuFlyoutItemKeyboardAcceleratorTextForeground", "AppBarButtonKeyboardAcceleratorTextForeground", "AppBarToggleButtonKeyboardAcceleratorTextForeground", "AppBarToggleButtonKeyboardAcceleratorTextForegroundChecked" } },
            { "SystemControlHighlightAccentBrush", new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed", "RadioButtonOuterEllipseCheckedStroke", "RadioButtonOuterEllipseCheckedStrokePointerOver", "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver", "CheckBoxCheckBackgroundFillChecked", "ToggleSwitchFillOn", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "CalendarViewSelectedBorderBrush", "TextControlBorderBrushFocused", "TextControlSelectionHighlightColor", "TextControlButtonBackgroundPressed", "TextControlButtonForegroundPointerOver", "GridViewItemBackgroundSelected", "AppBarToggleButtonBackgroundChecked", "AppBarToggleButtonBackgroundCheckedPointerOver", "AppBarToggleButtonBackgroundCheckedPressed", "SplitButtonBackgroundChecked", "SplitButtonBackgroundCheckedPointerOver" } },
            { "SystemControlForegroundBaseHighBrush", new[] { "SliderHeaderForeground", "ButtonForeground", "RadioButtonForeground", "RadioButtonForegroundPointerOver", "RadioButtonForegroundPressed", "CheckBoxForegroundUnchecked", "CheckBoxForegroundUncheckedPointerOver", "CheckBoxForegroundUncheckedPressed", "CheckBoxForegroundChecked", "CheckBoxForegroundCheckedPointerOver", "CheckBoxForegroundCheckedPressed", "CheckBoxForegroundIndeterminate", "CheckBoxForegroundIndeterminatePointerOver", "CheckBoxForegroundIndeterminatePressed", "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "RepeatButtonForeground", "ToggleSwitchContentForeground", "ToggleSwitchHeaderForeground", "ToggleButtonForeground", "ToggleButtonForegroundIndeterminate", "ScrollBarButtonArrowForeground", "ScrollBarButtonArrowForegroundPointerOver", "ComboBoxItemForeground", "ComboBoxForeground", "ComboBoxDropDownForeground", "AppBarEllipsisButtonForeground", "AppBarEllipsisButtonForegroundPressed", "AppBarForeground", "ToolTipForeground", "CalendarDatePickerForeground", "CalendarDatePickerTextForegroundSelected", "CalendarViewFocusBorderBrush", "CalendarViewCalendarItemForeground", "CalendarViewNavigationButtonForegroundPointerOver", "DatePickerHeaderForeground", "DatePickerButtonForeground", "TimePickerHeaderForeground", "TimePickerButtonForeground", "LoopingSelectorItemForeground", "TextControlForeground", "TextControlForegroundPointerOver", "TextControlHeaderForeground", "TextControlHighlighterForeground", "ToggleMenuFlyoutItemForeground", "GridViewItemForeground", "GridViewItemForegroundPointerOver", "GridViewItemForegroundSelected", "GridViewItemFocusSecondaryBorderBrush", "MenuFlyoutItemForeground", "MenuFlyoutSubItemForeground", "RatingControlPlaceholderForeground", "NavigationViewItemForeground", "TopNavigationViewItemForegroundSelected", "ColorPickerSliderThumbBackground", "AppBarButtonForeground", "AppBarToggleButtonForeground", "AppBarToggleButtonCheckGlyphForeground", "AppBarToggleButtonCheckGlyphForegroundChecked", "CommandBarForeground", "ListViewItemForeground", "ListViewItemFocusSecondaryBorderBrush", "TreeViewItemForeground", "SwipeItemForeground", "SplitButtonForeground" } },
            { "SystemControlDisabledBaseMediumLowBrush", new[] { "SliderHeaderForegroundDisabled", "SliderTickBarFillDisabled", "ButtonForegroundDisabled", "RadioButtonForegroundDisabled", "RadioButtonOuterEllipseStrokeDisabled", "RadioButtonOuterEllipseCheckedStrokeDisabled", "RadioButtonCheckGlyphFillDisabled", "CheckBoxForegroundUncheckedDisabled", "CheckBoxForegroundCheckedDisabled", "CheckBoxForegroundIndeterminateDisabled", "CheckBoxCheckBackgroundStrokeUncheckedDisabled", "CheckBoxCheckBackgroundStrokeCheckedDisabled", "CheckBoxCheckBackgroundStrokeIndeterminateDisabled", "CheckBoxCheckGlyphForegroundCheckedDisabled", "CheckBoxCheckGlyphForegroundIndeterminateDisabled", "HyperlinkButtonForegroundDisabled", "RepeatButtonForegroundDisabled", "ToggleSwitchContentForegroundDisabled", "ToggleSwitchHeaderForegroundDisabled", "ToggleSwitchStrokeOffDisabled", "ToggleSwitchStrokeOnDisabled", "ToggleSwitchKnobFillOffDisabled", "ToggleButtonForegroundDisabled", "ToggleButtonForegroundCheckedDisabled", "ToggleButtonForegroundIndeterminateDisabled", "ComboBoxItemForegroundDisabled", "ComboBoxItemForegroundSelectedDisabled", "ComboBoxForegroundDisabled", "ComboBoxDropDownGlyphForegroundDisabled", "AppBarEllipsisButtonForegroundDisabled", "AccentButtonForegroundDisabled", "CalendarDatePickerForegroundDisabled", "CalendarDatePickerCalendarGlyphForegroundDisabled", "CalendarDatePickerTextForegroundDisabled", "CalendarDatePickerHeaderForegroundDisabled", "CalendarViewBlackoutForeground", "CalendarViewWeekDayForegroundDisabled", "CalendarViewNavigationButtonForegroundDisabled", "HubSectionHeaderButtonForegroundDisabled", "DatePickerHeaderForegroundDisabled", "DatePickerButtonForegroundDisabled", "TimePickerHeaderForegroundDisabled", "TimePickerButtonForegroundDisabled", "TextControlHeaderForegroundDisabled", "ToggleMenuFlyoutItemForegroundDisabled", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "ToggleMenuFlyoutItemCheckGlyphForegroundDisabled", "PivotHeaderItemForegroundDisabled", "JumpListDefaultDisabledForeground", "MenuFlyoutItemForegroundDisabled", "MenuFlyoutSubItemForegroundDisabled", "MenuFlyoutSubItemChevronDisabled", "NavigationViewItemForegroundDisabled", "NavigationViewItemForegroundCheckedDisabled", "NavigationViewItemForegroundSelectedDisabled", "TopNavigationViewItemForegroundDisabled", "AppBarButtonForegroundDisabled", "AppBarToggleButtonForegroundDisabled", "AppBarToggleButtonCheckGlyphForegroundDisabled", "AppBarToggleButtonCheckGlyphForegroundCheckedDisabled", "AppBarToggleButtonOverflowLabelForegroundDisabled", "AppBarToggleButtonOverflowLabelForegroundCheckedDisabled", "CommandBarEllipsisIconForegroundDisabled", "TreeViewItemBackgroundSelectedDisabled", "TreeViewItemForegroundDisabled", "TreeViewItemForegroundSelectedDisabled", "SplitButtonForegroundDisabled", "SplitButtonForegroundCheckedDisabled", "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "AppBarButtonKeyboardAcceleratorTextForegroundDisabled", "AppBarToggleButtonKeyboardAcceleratorTextForegroundDisabled", "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedDisabled", "AppBarButtonSubItemChevronForegroundDisabled" } },
            { "SystemControlBackgroundAltHighBrush", new[] { "SliderInlineTickBarFill", "CalendarViewCalendarItemBackground", "CalendarViewBackground" } },
            { "SystemControlBackgroundBaseLowBrush", new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundDisabled", "RepeatButtonBackground", "RepeatButtonBackgroundPointerOver", "RepeatButtonBackgroundDisabled", "ThumbBackground", "ToggleButtonBackground", "ToggleButtonBackgroundPointerOver", "ToggleButtonBackgroundDisabled", "ToggleButtonBackgroundCheckedDisabled", "ToggleButtonBackgroundIndeterminate", "ToggleButtonBackgroundIndeterminatePointerOver", "ToggleButtonBackgroundIndeterminateDisabled", "ComboBoxBackgroundDisabled", "ContentDialogBorderBrush", "AccentButtonBackgroundDisabled", "CalendarDatePickerBackgroundPressed", "CalendarDatePickerBackgroundDisabled", "DatePickerButtonBackgroundPressed", "DatePickerButtonBackgroundDisabled", "TimePickerButtonBackgroundPressed", "TimePickerButtonBackgroundDisabled", "TextControlBackgroundDisabled", "JumpListDefaultDisabledBackground", "RatingControlUnselectedForeground", "SwipeItemBackground", "SwipeItemPreThresholdExecuteBackground", "SplitButtonBackground", "SplitButtonBackgroundPointerOver", "SplitButtonBackgroundDisabled", "SplitButtonBackgroundCheckedDisabled" } },
            { "SystemControlBackgroundBaseMediumLowBrush", new[] { "ButtonBackgroundPressed", "RepeatButtonBackgroundPressed", "ToggleButtonBackgroundPressed", "ToggleButtonBackgroundIndeterminatePressed", "ScrollBarThumbFillPointerOver", "AccentButtonBackgroundPressed", "FlipViewNextPreviousButtonBackground", "PivotNextButtonBackground", "PivotPreviousButtonBackground", "AppBarToggleButtonForegroundCheckedDisabled", "SwipeItemBackgroundPressed", "SplitButtonBackgroundPressed" } },
            { "SystemControlHighlightBaseHighBrush", new[] { "ButtonForegroundPointerOver", "ButtonForegroundPressed", "RadioButtonOuterEllipseStrokePointerOver", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "RepeatButtonForegroundPointerOver", "RepeatButtonForegroundPressed", "ToggleSwitchStrokeOffPointerOver", "ToggleSwitchStrokeOn", "ToggleSwitchKnobFillOffPointerOver", "ToggleButtonForegroundPointerOver", "ToggleButtonForegroundPressed", "ToggleButtonForegroundIndeterminatePointerOver", "ToggleButtonForegroundIndeterminatePressed", "AccentButtonForegroundPressed", "CalendarViewSelectedForeground", "CalendarViewPressedForeground", "DatePickerButtonForegroundPointerOver", "DatePickerButtonForegroundPressed", "TimePickerButtonForegroundPointerOver", "TimePickerButtonForegroundPressed", "SplitButtonForegroundPointerOver", "SplitButtonForegroundPressed" } },
            { "SystemControlForegroundTransparentBrush", new[] { "ButtonBorderBrush", "RepeatButtonBorderBrush", "ToggleButtonBorderBrush", "ToggleButtonBorderBrushIndeterminate", "ScrollBarTrackStroke", "ScrollBarTrackStrokePointerOver", "AppBarHighContrastBorder", "AccentButtonBorderBrush", "FlipViewNextPreviousButtonBorderBrush", "FlipViewNextPreviousButtonBorderBrushPointerOver", "FlipViewNextPreviousButtonBorderBrushPressed", "DateTimePickerFlyoutButtonBorderBrush", "PivotNextButtonBorderBrush", "PivotNextButtonBorderBrushPointerOver", "PivotNextButtonBorderBrushPressed", "PivotPreviousButtonBorderBrush", "PivotPreviousButtonBorderBrushPointerOver", "PivotPreviousButtonBorderBrushPressed", "KeyTipBorderBrush", "CommandBarHighContrastBorder", "SplitButtonBorderBrush" } },
            { "SystemControlHighlightBaseMediumLowBrush", new[] { "ButtonBorderBrushPointerOver", "HyperlinkButtonForegroundPressed", "RepeatButtonBorderBrushPointerOver", "ThumbBackgroundPointerOver", "ToggleButtonBackgroundCheckedPressed", "ToggleButtonBorderBrushPointerOver", "ToggleButtonBorderBrushCheckedPointerOver", "ToggleButtonBorderBrushIndeterminatePointerOver", "ComboBoxBackgroundBorderBrushUnfocused", "ComboBoxBorderBrushPressed", "AccentButtonBorderBrushPointerOver", "CalendarDatePickerBorderBrushPressed", "CalendarViewHoverBorderBrush", "HubSectionHeaderButtonForegroundPressed", "DatePickerButtonBorderBrushPressed", "TimePickerButtonBorderBrushPressed", "MenuBarItemBorderBrushPressed", "MenuBarItemBorderBrushSelected", "SplitButtonBackgroundCheckedPressed", "SplitButtonBorderBrushPointerOver", "SplitButtonBorderBrushCheckedPointerOver" } },
            { "SystemControlHighlightTransparentBrush", new[] { "ButtonBorderBrushPressed", "RadioButtonOuterEllipseCheckedFillPointerOver", "RadioButtonOuterEllipseCheckedFillPressed", "CheckBoxCheckBackgroundStrokeUncheckedPressed", "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPressed", "CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckBackgroundFillIndeterminatePointerOver", "CheckBoxCheckBackgroundFillIndeterminatePressed", "RepeatButtonBorderBrushPressed", "ThumbBorderBrush", "ThumbBorderBrushPointerOver", "ThumbBorderBrushPressed", "ToggleButtonBorderBrushPressed", "ToggleButtonBorderBrushIndeterminatePressed", "ComboBoxBackgroundBorderBrushFocused", "AccentButtonBorderBrushPressed", "CalendarViewNavigationButtonBorderBrushPointerOver", "DateTimePickerFlyoutButtonBorderBrushPointerOver", "DateTimePickerFlyoutButtonBorderBrushPressed", "PivotHeaderItemBackgroundUnselectedPointerOver", "PivotHeaderItemBackgroundUnselectedPressed", "PivotHeaderItemBackgroundSelected", "PivotHeaderItemBackgroundSelectedPointerOver", "PivotHeaderItemBackgroundSelectedPressed", "SplitButtonBorderBrushPressed" } },
            { "SystemControlDisabledTransparentBrush", new[] { "ButtonBorderBrushDisabled", "RepeatButtonBorderBrushDisabled", "ToggleButtonBorderBrushDisabled", "ToggleButtonBorderBrushCheckedDisabled", "ToggleButtonBorderBrushIndeterminateDisabled", "ScrollBarThumbFillDisabled", "ScrollBarTrackFillDisabled", "ScrollBarTrackStrokeDisabled", "AccentButtonBorderBrushDisabled", "SplitButtonBorderBrushDisabled", "SplitButtonBorderBrushCheckedDisabled" } },
            { "SystemControlForegroundBaseMediumHighBrush", new[] { "RadioButtonOuterEllipseStroke", "CheckBoxCheckBackgroundStrokeUnchecked", "CheckBoxCheckGlyphForegroundIndeterminate", "ToggleSwitchStrokeOff", "ToggleSwitchStrokeOffPressed", "ToggleSwitchKnobFillOff", "ComboBoxDropDownGlyphForeground", "ComboBoxEditableDropDownGlyphForeground", "CalendarDatePickerCalendarGlyphForeground", "ToggleMenuFlyoutItemCheckGlyphForeground", "GridViewItemCheckBrush", "MenuFlyoutSubItemChevron", "TopNavigationViewItemForegroundPointerOver", "TopNavigationViewItemForegroundPressed", "ListViewItemCheckBrush", "ListViewItemCheckBoxBrush", "TreeViewItemCheckBoxBorderSelected", "TreeViewItemCheckGlyphSelected", "AppBarButtonSubItemChevronForeground" } },
            { "SystemControlHighlightBaseMediumBrush", new[] { "RadioButtonOuterEllipseStrokePressed", "RadioButtonOuterEllipseCheckedStrokePressed", "CheckBoxCheckBackgroundStrokeIndeterminatePressed", "CheckBoxCheckBackgroundFillCheckedPressed", "ToggleSwitchFillOffPressed", "ToggleSwitchFillOnPressed", "ToggleSwitchStrokeOnPressed", "ThumbBackgroundPressed", "ComboBoxBorderBrushPointerOver", "CalendarDatePickerBorderBrushPointerOver", "CalendarViewPressedBorderBrush", "FlipViewNextPreviousButtonBackgroundPointerOver", "DatePickerButtonBorderBrushPointerOver", "TimePickerButtonBorderBrushPointerOver", "TextControlBorderBrushPointerOver", "PivotNextButtonBackgroundPointerOver", "PivotPreviousButtonBackgroundPointerOver", "MenuBarItemBorderBrushPointerOver" } },
            { "SystemControlHighlightAltTransparentBrush", new[] { "RadioButtonOuterEllipseCheckedFill", "ToggleButtonBorderBrushChecked", "SplitButtonBorderBrushChecked", "SplitButtonBorderBrushCheckedPressed" } },
            { "SystemControlHighlightBaseMediumHighBrush", new[] { "RadioButtonCheckGlyphFill", "FlipViewNextPreviousButtonBackgroundPressed", "PivotNextButtonBackgroundPressed", "PivotPreviousButtonBackgroundPressed" } },
            { "SystemControlHighlightAltBaseHighBrush", new[] { "RadioButtonCheckGlyphFillPointerOver", "ComboBoxItemForegroundPressed", "ComboBoxItemForegroundPointerOver", "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedUnfocused", "ComboBoxItemForegroundSelectedPressed", "ComboBoxItemForegroundSelectedPointerOver", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed", "ComboBoxPlaceHolderForegroundFocusedPressed", "AppBarEllipsisButtonForegroundPointerOver", "DateTimePickerFlyoutButtonForegroundPointerOver", "DateTimePickerFlyoutButtonForegroundPressed", "DatePickerButtonForegroundFocused", "TimePickerButtonForegroundFocused", "LoopingSelectorItemForegroundSelected", "LoopingSelectorItemForegroundPointerOver", "LoopingSelectorItemForegroundPressed", "ToggleMenuFlyoutItemForegroundPointerOver", "ToggleMenuFlyoutItemForegroundPressed", "ToggleMenuFlyoutItemCheckGlyphForegroundPointerOver", "ToggleMenuFlyoutItemCheckGlyphForegroundPressed", "PivotHeaderItemForegroundSelected", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed", "MenuFlyoutSubItemForegroundPointerOver", "MenuFlyoutSubItemForegroundPressed", "MenuFlyoutSubItemForegroundSubMenuOpened", "MenuFlyoutSubItemChevronPointerOver", "MenuFlyoutSubItemChevronPressed", "MenuFlyoutSubItemChevronSubMenuOpened", "NavigationViewItemForegroundPointerOver", "NavigationViewItemForegroundPressed", "NavigationViewItemForegroundChecked", "NavigationViewItemForegroundCheckedPointerOver", "NavigationViewItemForegroundCheckedPressed", "NavigationViewItemForegroundSelected", "NavigationViewItemForegroundSelectedPointerOver", "NavigationViewItemForegroundSelectedPressed", "AppBarButtonForegroundPointerOver", "AppBarButtonForegroundPressed", "AppBarToggleButtonForegroundPointerOver", "AppBarToggleButtonForegroundPressed", "AppBarToggleButtonForegroundChecked", "AppBarToggleButtonForegroundCheckedPointerOver", "AppBarToggleButtonForegroundCheckedPressed", "AppBarToggleButtonCheckGlyphForegroundPointerOver", "AppBarToggleButtonCheckGlyphForegroundPressed", "AppBarToggleButtonCheckGlyphForegroundCheckedPointerOver", "AppBarToggleButtonCheckGlyphForegroundCheckedPressed", "AppBarToggleButtonOverflowLabelForegroundPointerOver", "AppBarToggleButtonOverflowLabelForegroundPressed", "AppBarToggleButtonOverflowLabelForegroundCheckedPointerOver", "AppBarToggleButtonOverflowLabelForegroundCheckedPressed", "ListViewItemForegroundPointerOver", "ListViewItemForegroundSelected", "TreeViewItemForegroundPointerOver", "TreeViewItemForegroundPressed", "TreeViewItemForegroundSelected", "TreeViewItemForegroundSelectedPointerOver", "TreeViewItemForegroundSelectedPressed", "AppBarButtonForegroundSubMenuOpened", "AppBarButtonSubItemChevronForegroundPointerOver", "AppBarButtonSubItemChevronForegroundPressed", "AppBarButtonSubItemChevronForegroundSubMenuOpened" } },
            { "SystemControlHighlightAltBaseMediumBrush", new[] { "RadioButtonCheckGlyphFillPressed", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", "AppBarButtonKeyboardAcceleratorTextForegroundPointerOver", "AppBarButtonKeyboardAcceleratorTextForegroundPressed", "AppBarToggleButtonKeyboardAcceleratorTextForegroundPointerOver", "AppBarToggleButtonKeyboardAcceleratorTextForegroundPressed", "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedPointerOver", "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedPressed", "AppBarButtonKeyboardAcceleratorTextForegroundSubMenuOpened" } },
            { "SystemControlBackgroundBaseMediumBrush", new[] { "CheckBoxCheckBackgroundFillUncheckedPressed", "ScrollBarButtonBackgroundPressed", "ScrollBarThumbFillPressed", "SwipeItemPreThresholdExecuteForeground" } },
            { "SystemControlBackgroundAccentBrush", new[] { "CheckBoxCheckBackgroundFillCheckedPointerOver", "JumpListDefaultEnabledBackground", "SwipeItemPostThresholdExecuteBackground" } },
            { "SystemControlHighlightAltChromeWhiteBrush", new[] { "CheckBoxCheckGlyphForegroundUnchecked", "CheckBoxCheckGlyphForegroundUncheckedPointerOver", "CheckBoxCheckGlyphForegroundUncheckedPressed", "CheckBoxCheckGlyphForegroundUncheckedDisabled", "CheckBoxCheckGlyphForegroundChecked", "ToggleSwitchKnobFillOffPressed", "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPressed", "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed", "CalendarViewTodayForeground", "TextControlButtonForegroundPressed", "GridViewItemDragForeground", "ListViewItemDragForeground", "SplitButtonForegroundChecked", "SplitButtonForegroundCheckedPointerOver", "SplitButtonForegroundCheckedPressed" } },
            { "SystemControlForegroundChromeWhiteBrush", new[] { "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed", "JumpListDefaultEnabledForeground", "SwipeItemPostThresholdExecuteForeground" } },
            { "SystemControlHyperlinkTextBrush", new[] { "HyperlinkButtonForeground", "HubSectionHeaderButtonForeground", "ContentLinkForegroundColor" } },
            { "SystemControlPageBackgroundTransparentBrush", new[] { "HyperlinkButtonBackground", "HyperlinkButtonBackgroundPointerOver", "HyperlinkButtonBackgroundPressed", "HyperlinkButtonBackgroundDisabled" } },
            { "SystemControlHighlightAltListAccentHighBrush", new[] { "ToggleSwitchFillOnPointerOver" } },
            { "SystemControlDisabledBaseLowBrush", new[] { "ToggleSwitchFillOnDisabled", "ComboBoxBorderBrushDisabled", "CalendarDatePickerBorderBrushDisabled", "DatePickerSpacerFillDisabled", "DatePickerButtonBorderBrushDisabled", "TimePickerSpacerFillDisabled", "TimePickerButtonBorderBrushDisabled", "TextControlBorderBrushDisabled", "ColorPickerSliderTrackFillDisabled" } },
            { "SystemControlHighlightListAccentHighBrush", new[] { "ToggleSwitchStrokeOnPointerOver", "ComboBoxItemBackgroundSelectedPressed", "CalendarViewSelectedPressedBorderBrush", "GridViewItemBackgroundSelectedPressed", "MenuFlyoutSubItemBackgroundPressed", "ListViewItemBackgroundSelectedPressed" } },
            { "SystemControlHighlightChromeWhiteBrush", new[] { "ToggleSwitchKnobFillOnPointerOver" } },
            { "SystemControlPageBackgroundBaseLowBrush", new[] { "ToggleSwitchKnobFillOnDisabled" } },
            { "SystemControlBackgroundListLowBrush", new[] { "ScrollBarButtonBackgroundPointerOver", "ComboBoxDropDownBackgroundPointerOver", "MenuBarItemBackgroundPointerOver" } },
            { "SystemControlForegroundAltHighBrush", new[] { "ScrollBarButtonArrowForegroundPressed", "GridViewItemFocusBorderBrush", "ListViewItemFocusBorderBrush" } },
            { "SystemControlForegroundBaseLowBrush", new[] { "ScrollBarButtonArrowForegroundDisabled", "ListViewHeaderItemDividerStroke", "DatePickerSpacerFill", "DatePickerFlyoutPresenterSpacerFill", "TimePickerSpacerFill", "TimePickerFlyoutPresenterSpacerFill", "GridViewHeaderItemDividerStroke" } },
            { "SystemControlForegroundChromeDisabledLowBrush", new[] { "ScrollBarThumbFill" } },
            { "SystemControlDisabledChromeHighBrush", new[] { "ScrollBarPanningThumbBackgroundDisabled" } },
            { "SystemBaseLowColor", new[] { "ScrollBarThumbBackgroundColor" } },
            { "SystemChromeDisabledLowColor", new[] { "ScrollBarPanningThumbBackgroundColor" } },
            { "SystemControlHighlightListMediumBrush", new[] { "ComboBoxItemBackgroundPressed", "AppBarEllipsisButtonBackgroundPressed", "DateTimePickerFlyoutButtonBackgroundPressed", "LoopingSelectorItemBackgroundPressed", "ToggleMenuFlyoutItemBackgroundPressed", "GridViewItemBackgroundPressed", "MenuFlyoutItemBackgroundPressed", "AppBarButtonBackgroundPressed", "AppBarToggleButtonBackgroundHighLightOverlayPressed", "AppBarToggleButtonBackgroundHighLightOverlayCheckedPressed", "ListViewItemBackgroundPressed" } },
            { "SystemControlHighlightListLowBrush", new[] { "ComboBoxItemBackgroundPointerOver", "AppBarEllipsisButtonBackgroundPointerOver", "DateTimePickerFlyoutButtonBackgroundPointerOver", "LoopingSelectorItemBackgroundPointerOver", "ToggleMenuFlyoutItemBackgroundPointerOver", "GridViewItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutSubItemBackgroundPointerOver", "AppBarButtonBackgroundPointerOver", "AppBarToggleButtonBackgroundHighLightOverlayPointerOver", "AppBarToggleButtonBackgroundHighLightOverlayCheckedPointerOver", "ListViewItemBackgroundPointerOver" } },
            { "SystemControlHighlightListAccentLowBrush", new[] { "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedUnfocused", "ComboBoxBackgroundUnfocused", "CalendarDatePickerBackgroundFocused", "DatePickerButtonBackgroundFocused", "DatePickerFlyoutPresenterHighlightFill", "TimePickerButtonBackgroundFocused", "TimePickerFlyoutPresenterHighlightFill", "MenuFlyoutSubItemBackgroundSubMenuOpened", "ListViewItemBackgroundSelected", "AppBarButtonBackgroundSubMenuOpened" } },
            { "SystemControlHighlightListAccentMediumBrush", new[] { "ComboBoxItemBackgroundSelectedPointerOver", "CalendarViewSelectedHoverBorderBrush", "GridViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPointerOver" } },
            { "SystemControlBackgroundAltMediumLowBrush", new[] { "ComboBoxBackground", "CalendarDatePickerBackground", "DatePickerButtonBackground", "TimePickerButtonBackground", "TextControlBackground" } },
            { "SystemControlPageBackgroundAltMediumBrush", new[] { "ComboBoxBackgroundPointerOver", "CalendarDatePickerBackgroundPointerOver", "DatePickerButtonBackgroundPointerOver", "TimePickerButtonBackgroundPointerOver", "MediaTransportControlsPanelBackground" } },
            { "SystemControlBackgroundListMediumBrush", new[] { "ComboBoxBackgroundPressed", "ComboBoxDropDownBackgroundPointerPressed", "MenuBarItemBackgroundPressed", "MenuBarItemBackgroundSelected" } },
            { "SystemControlPageTextBaseHighBrush", new[] { "ComboBoxPlaceHolderForeground", "ContentDialogForeground", "HubForeground", "HubSectionHeaderForeground" } },
            { "SystemControlBackgroundChromeBlackLowBrush", new[] { "ComboBoxFocusedDropDownBackgroundPointerOver" } },
            { "SystemControlBackgroundChromeBlackMediumLowBrush", new[] { "ComboBoxFocusedDropDownBackgroundPointerPressed" } },
            { "SystemControlHighlightAltBaseMediumHighBrush", new[] { "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed", "PivotHeaderItemForegroundUnselectedPointerOver", "PivotHeaderItemForegroundUnselectedPressed", "PivotHeaderItemForegroundSelectedPointerOver", "PivotHeaderItemForegroundSelectedPressed" } },
            { "SystemControlTransientBackgroundBrush", new[] { "ComboBoxDropDownBackground", "DatePickerFlyoutPresenterBackground", "TimePickerFlyoutPresenterBackground", "FlyoutPresenterBackground", "MediaTransportControlsFlyoutBackground", "MenuFlyoutPresenterBackground", "CommandBarOverflowPresenterBackground", "AutoSuggestBoxSuggestionsListBackground" } },
            { "SystemControlTransientBorderBrush", new[] { "ComboBoxDropDownBorderBrush", "ToolTipBorderBrush", "DatePickerFlyoutPresenterBorderBrush", "TimePickerFlyoutPresenterBorderBrush", "FlyoutBorderThemeBrush", "MenuFlyoutPresenterBorderBrush", "CommandBarOverflowPresenterBorderBrush", "AutoSuggestBoxSuggestionsListBorderBrush" } },
            { "SystemControlBackgroundChromeMediumBrush", new[] { "AppBarBackground", "LoopingSelectorButtonBackground", "GridViewItemCheckBoxBrush", "CommandBarBackground" } },
            { "SystemControlPageBackgroundAltHighBrush", new[] { "ContentDialogBackground" } },
            { "SystemControlBackgroundChromeWhiteBrush", new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "TextControlBackgroundFocused", "KeyTipForeground" } },
            { "SystemControlBackgroundChromeMediumLowBrush", new[] { "ToolTipBackground" } },
            { "SystemControlHyperlinkBaseHighBrush", new[] { "CalendarViewOutOfScopeForeground" } },
            { "SystemControlDisabledChromeMediumLowBrush", new[] { "CalendarViewOutOfScopeBackground" } },
            { "SystemControlHyperlinkBaseMediumHighBrush", new[] { "CalendarViewForeground" } },
            { "SystemControlForegroundChromeMediumBrush", new[] { "CalendarViewBorderBrush" } },
            { "SystemControlHyperlinkBaseMediumBrush", new[] { "HubSectionHeaderButtonForegroundPointerOver" } },
            { "SystemControlPageBackgroundListLowBrush", new[] { "FlipViewBackground" } },
            { "SystemControlForegroundAltMediumHighBrush", new[] { "FlipViewNextPreviousArrowForeground", "PivotNextButtonForeground", "PivotPreviousButtonForeground" } },
            { "SystemControlHighlightAltAltMediumHighBrush", new[] { "FlipViewNextPreviousArrowForegroundPointerOver", "FlipViewNextPreviousArrowForegroundPressed", "PivotNextButtonForegroundPointerOver", "PivotNextButtonForegroundPressed", "PivotPreviousButtonForegroundPointerOver", "PivotPreviousButtonForegroundPressed" } },
            { "SystemControlForegroundChromeBlackHighBrush", new[] { "TextControlForegroundFocused" } },
            { "SystemControlDisabledChromeDisabledLowBrush", new[] { "TextControlForegroundDisabled", "TextControlPlaceholderForegroundDisabled" } },
            { "SystemControlBackgroundAltMediumBrush", new[] { "TextControlBackgroundPointerOver" } },
            { "SystemControlPageTextChromeBlackMediumLowBrush", new[] { "TextControlPlaceholderForegroundFocused" } },
            { "SystemControlForegroundChromeBlackMediumBrush", new[] { "TextControlButtonForeground" } },
            { "SystemControlPageBackgroundChromeLowBrush", new[] { "ContentLinkBackgroundColor" } },
            { "SystemControlHighlightAltAccentBrush", new[] { "PivotHeaderItemFocusPipeFill", "PivotHeaderItemSelectedPipeFill" } },
            { "SystemControlFocusVisualPrimaryBrush", new[] { "GridViewItemFocusVisualPrimaryBrush", "ListViewItemFocusVisualPrimaryBrush" } },
            { "SystemControlFocusVisualSecondaryBrush", new[] { "GridViewItemFocusVisualSecondaryBrush", "ListViewItemFocusVisualSecondaryBrush" } },
            { "SystemControlPageBackgroundMediumAltMediumBrush", new[] { "AppBarLightDismissOverlayBackground", "CalendarDatePickerLightDismissOverlayBackground", "ComboBoxLightDismissOverlayBackground", "DatePickerLightDismissOverlayBackground", "FlyoutLightDismissOverlayBackground", "PopupLightDismissOverlayBackground", "SplitViewLightDismissOverlayBackground", "TimePickerLightDismissOverlayBackground", "MenuFlyoutLightDismissOverlayBackground", "CommandBarLightDismissOverlayBackground", "AutoSuggestBoxLightDismissOverlayBackground" } },
            { "SystemControlForegroundChromeGrayBrush", new[] { "KeyTipBackground" } },
            { "SystemBaseMediumLowColor", new[] { "RatingControlDisabledSelectedForeground" } },
            { "SystemControlChromeMediumLowAcrylicElementMediumBrush", new[] { "NavigationViewDefaultPaneBackground", "NavigationViewTopPaneBackground" } },
            { "SystemControlForegroundChromeHighBrush", new[] { "ColorPickerSliderThumbBackgroundPressed" } },
            { "SystemControlDisabledAccentBrush", new[] { "AppBarToggleButtonBackgroundCheckedDisabled" } },
        };

        private readonly Dictionary<string, string[]> _mappingDark = new Dictionary<string, string[]>
        {
            { "SystemControlPageTextBaseMediumBrush", new[] { "SystemControlDescriptionTextForegroundBrush", "HyperlinkButtonForegroundPointerOver", "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver" } },
            { "SystemControlTransparentBrush", new[] { "SliderContainerBackground", "SliderContainerBackgroundPointerOver", "SliderContainerBackgroundPressed", "SliderContainerBackgroundDisabled", "RadioButtonBackground", "RadioButtonBackgroundPointerOver", "RadioButtonBackgroundPressed", "RadioButtonBackgroundDisabled", "RadioButtonBorderBrush", "RadioButtonBorderBrushPointerOver", "RadioButtonBorderBrushPressed", "RadioButtonBorderBrushDisabled", "RadioButtonOuterEllipseFill", "RadioButtonOuterEllipseFillPointerOver", "RadioButtonOuterEllipseFillPressed", "RadioButtonOuterEllipseFillDisabled", "RadioButtonOuterEllipseCheckedFillDisabled", "RadioButtonCheckGlyphStroke", "RadioButtonCheckGlyphStrokePointerOver", "RadioButtonCheckGlyphStrokePressed", "RadioButtonCheckGlyphStrokeDisabled", "CheckBoxBackgroundUnchecked", "CheckBoxBackgroundUncheckedPointerOver", "CheckBoxBackgroundUncheckedPressed", "CheckBoxBackgroundUncheckedDisabled", "CheckBoxBackgroundChecked", "CheckBoxBackgroundCheckedPointerOver", "CheckBoxBackgroundCheckedPressed", "CheckBoxBackgroundCheckedDisabled", "CheckBoxBackgroundIndeterminate", "CheckBoxBackgroundIndeterminatePointerOver", "CheckBoxBackgroundIndeterminatePressed", "CheckBoxBackgroundIndeterminateDisabled", "CheckBoxBorderBrushUnchecked", "CheckBoxBorderBrushUncheckedPointerOver", "CheckBoxBorderBrushUncheckedPressed", "CheckBoxBorderBrushUncheckedDisabled", "CheckBoxBorderBrushChecked", "CheckBoxBorderBrushCheckedPointerOver", "CheckBoxBorderBrushCheckedPressed", "CheckBoxBorderBrushCheckedDisabled", "CheckBoxBorderBrushIndeterminate", "CheckBoxBorderBrushIndeterminatePointerOver", "CheckBoxBorderBrushIndeterminatePressed", "CheckBoxBorderBrushIndeterminateDisabled", "CheckBoxCheckBackgroundFillUnchecked", "CheckBoxCheckBackgroundFillUncheckedPointerOver", "CheckBoxCheckBackgroundFillUncheckedDisabled", "CheckBoxCheckBackgroundFillCheckedDisabled", "CheckBoxCheckBackgroundFillIndeterminateDisabled", "HyperlinkButtonBorderBrush", "HyperlinkButtonBorderBrushPointerOver", "HyperlinkButtonBorderBrushPressed", "HyperlinkButtonBorderBrushDisabled", "ToggleSwitchContainerBackground", "ToggleSwitchContainerBackgroundPointerOver", "ToggleSwitchContainerBackgroundPressed", "ToggleSwitchContainerBackgroundDisabled", "ToggleSwitchFillOff", "ToggleSwitchFillOffPointerOver", "ToggleSwitchFillOffDisabled", "ToggleButtonBorderBrushCheckedPressed", "ScrollBarBackground", "ScrollBarBackgroundPointerOver", "ScrollBarBackgroundDisabled", "ScrollBarForeground", "ScrollBarBorderBrush", "ScrollBarBorderBrushPointerOver", "ScrollBarBorderBrushDisabled", "ScrollBarButtonBackground", "ScrollBarButtonBackgroundDisabled", "ScrollBarButtonBorderBrush", "ScrollBarButtonBorderBrushPointerOver", "ScrollBarButtonBorderBrushPressed", "ScrollBarButtonBorderBrushDisabled", "ListViewHeaderItemBackground", "ComboBoxItemBackground", "ComboBoxItemBackgroundDisabled", "ComboBoxItemBackgroundSelectedDisabled", "ComboBoxItemBorderBrush", "ComboBoxItemBorderBrushPressed", "ComboBoxItemBorderBrushPointerOver", "ComboBoxItemBorderBrushDisabled", "ComboBoxItemBorderBrushSelected", "ComboBoxItemBorderBrushSelectedUnfocused", "ComboBoxItemBorderBrushSelectedPressed", "ComboBoxItemBorderBrushSelectedPointerOver", "ComboBoxItemBorderBrushSelectedDisabled", "AppBarEllipsisButtonBackground", "AppBarEllipsisButtonBackgroundDisabled", "AppBarEllipsisButtonBorderBrush", "AppBarEllipsisButtonBorderBrushPointerOver", "AppBarEllipsisButtonBorderBrushPressed", "AppBarEllipsisButtonBorderBrushDisabled", "CalendarViewNavigationButtonBackground", "CalendarViewNavigationButtonBorderBrush", "FlipViewItemBackground", "DateTimePickerFlyoutButtonBackground", "TextControlButtonBackground", "TextControlButtonBackgroundPointerOver", "TextControlButtonBorderBrush", "TextControlButtonBorderBrushPointerOver", "TextControlButtonBorderBrushPressed", "ToggleMenuFlyoutItemBackground", "ToggleMenuFlyoutItemBackgroundDisabled", "PivotBackground", "PivotHeaderBackground", "PivotItemBackground", "PivotHeaderItemBackgroundUnselected", "PivotHeaderItemBackgroundDisabled", "GridViewHeaderItemBackground", "GridViewItemBackground", "GridViewItemDragBackground", "MenuFlyoutItemBackground", "MenuFlyoutItemBackgroundDisabled", "MenuFlyoutSubItemBackground", "MenuFlyoutSubItemBackgroundDisabled", "NavigationViewItemBorderBrushDisabled", "NavigationViewItemBorderBrushCheckedDisabled", "NavigationViewItemBorderBrushSelectedDisabled", "TopNavigationViewItemBackgroundPointerOver", "TopNavigationViewItemBackgroundPressed", "TopNavigationViewItemBackgroundSelected", "NavigationViewBackButtonBackground", "MenuBarBackground", "MenuBarItemBackground", "AppBarButtonBackground", "AppBarButtonBackgroundDisabled", "AppBarButtonBorderBrush", "AppBarButtonBorderBrushPointerOver", "AppBarButtonBorderBrushPressed", "AppBarButtonBorderBrushDisabled", "AppBarToggleButtonBackground", "AppBarToggleButtonBackgroundDisabled", "AppBarToggleButtonBackgroundHighLightOverlay", "AppBarToggleButtonBorderBrush", "AppBarToggleButtonBorderBrushPointerOver", "AppBarToggleButtonBorderBrushPressed", "AppBarToggleButtonBorderBrushDisabled", "AppBarToggleButtonBorderBrushChecked", "AppBarToggleButtonBorderBrushCheckedPointerOver", "AppBarToggleButtonBorderBrushCheckedPressed", "AppBarToggleButtonBorderBrushCheckedDisabled", "ListViewItemBackground", "ListViewItemDragBackground", "TreeViewItemBackgroundDisabled", "TreeViewItemBorderBrush", "TreeViewItemBorderBrushDisabled", "TreeViewItemBorderBrushSelected", "TreeViewItemBorderBrushSelectedDisabled", "TreeViewItemCheckBoxBackgroundSelected", "CommandBarFlyoutButtonBackground", "AppBarButtonBorderBrushSubMenuOpened" } },
            { "SystemControlForegroundAccentBrush", new[] { "SliderThumbBackground", "CheckBoxCheckBackgroundStrokeIndeterminate", "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "RatingControlSelectedForeground", "RatingControlPointerOverSelectedForeground", "NavigationViewSelectionIndicatorForeground" } },
            { "SystemControlHighlightChromeAltLowBrush", new[] { "SliderThumbBackgroundPointerOver", "ContentLinkBackgroundColor", "ColorPickerSliderThumbBackgroundPointerOver" } },
            { "SystemControlHighlightChromeHighBrush", new[] { "SliderThumbBackgroundPressed" } },
            { "SystemControlDisabledChromeDisabledHighBrush", new[] { "SliderThumbBackgroundDisabled", "SliderTrackFillDisabled", "SliderTrackValueFillDisabled", "GridViewItemPlaceholderBackground", "ColorPickerSliderThumbBackgroundDisabled", "ListViewItemPlaceholderBackground" } },
            { "SystemControlForegroundBaseMediumLowBrush", new[] { "SliderTrackFill", "SliderTrackFillPressed", "SliderTickBarFill", "ComboBoxBorderBrush", "AppBarSeparatorForeground", "CalendarDatePickerBorderBrush", "DatePickerButtonBorderBrush", "TimePickerButtonBorderBrush", "TextControlBorderBrush", "MenuBarItemBorderBrush" } },
            { "SystemControlForegroundBaseMediumBrush", new[] { "SliderTrackFillPointerOver", "CheckBoxCheckGlyphForegroundIndeterminatePressed", "CalendarDatePickerTextForeground", "CalendarViewNavigationButtonForegroundPressed", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForeground", "PivotHeaderItemForegroundUnselected", "RatingControlPointerOverPlaceholderForeground", "RatingControlPointerOverUnselectedForeground", "RatingControlCaptionForeground", "TopNavigationViewItemForeground", "MenuFlyoutItemKeyboardAcceleratorTextForeground", "AppBarButtonKeyboardAcceleratorTextForeground", "AppBarToggleButtonKeyboardAcceleratorTextForeground", "AppBarToggleButtonKeyboardAcceleratorTextForegroundChecked" } },
            { "SystemControlHighlightAccentBrush", new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed", "RadioButtonOuterEllipseCheckedStroke", "RadioButtonOuterEllipseCheckedStrokePointerOver", "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver", "CheckBoxCheckBackgroundFillChecked", "ToggleSwitchFillOn", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "CalendarViewSelectedBorderBrush", "TextControlBorderBrushFocused", "TextControlSelectionHighlightColor", "TextControlButtonBackgroundPressed", "TextControlButtonForegroundPointerOver", "GridViewItemBackgroundSelected", "AppBarToggleButtonBackgroundChecked", "AppBarToggleButtonBackgroundCheckedPointerOver", "AppBarToggleButtonBackgroundCheckedPressed", "SplitButtonBackgroundChecked", "SplitButtonBackgroundCheckedPointerOver" } },
            { "SystemControlForegroundBaseHighBrush", new[] { "SliderHeaderForeground", "ButtonForeground", "RadioButtonForeground", "RadioButtonForegroundPointerOver", "RadioButtonForegroundPressed", "CheckBoxForegroundUnchecked", "CheckBoxForegroundUncheckedPointerOver", "CheckBoxForegroundUncheckedPressed", "CheckBoxForegroundChecked", "CheckBoxForegroundCheckedPointerOver", "CheckBoxForegroundCheckedPressed", "CheckBoxForegroundIndeterminate", "CheckBoxForegroundIndeterminatePointerOver", "CheckBoxForegroundIndeterminatePressed", "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "RepeatButtonForeground", "ToggleSwitchContentForeground", "ToggleSwitchHeaderForeground", "ToggleButtonForeground", "ToggleButtonForegroundIndeterminate", "ScrollBarButtonArrowForeground", "ScrollBarButtonArrowForegroundPointerOver", "ComboBoxItemForeground", "ComboBoxForeground", "ComboBoxDropDownForeground", "AppBarEllipsisButtonForeground", "AppBarEllipsisButtonForegroundPressed", "AppBarForeground", "ToolTipForeground", "CalendarDatePickerForeground", "CalendarDatePickerTextForegroundSelected", "CalendarViewFocusBorderBrush", "CalendarViewCalendarItemForeground", "CalendarViewNavigationButtonForegroundPointerOver", "DatePickerHeaderForeground", "DatePickerButtonForeground", "TimePickerHeaderForeground", "TimePickerButtonForeground", "LoopingSelectorItemForeground", "TextControlForeground", "TextControlForegroundPointerOver", "TextControlHeaderForeground", "ToggleMenuFlyoutItemForeground", "GridViewItemForeground", "GridViewItemForegroundPointerOver", "GridViewItemForegroundSelected", "GridViewItemFocusSecondaryBorderBrush", "MenuFlyoutItemForeground", "MenuFlyoutSubItemForeground", "RatingControlPlaceholderForeground", "NavigationViewItemForeground", "TopNavigationViewItemForegroundSelected", "ColorPickerSliderThumbBackground", "AppBarButtonForeground", "AppBarToggleButtonForeground", "AppBarToggleButtonCheckGlyphForeground", "AppBarToggleButtonCheckGlyphForegroundChecked", "CommandBarForeground", "ListViewItemForeground", "ListViewItemFocusSecondaryBorderBrush", "TreeViewItemForeground", "SwipeItemForeground", "SplitButtonForeground" } },
            { "SystemControlDisabledBaseMediumLowBrush", new[] { "SliderHeaderForegroundDisabled", "SliderTickBarFillDisabled", "ButtonForegroundDisabled", "RadioButtonForegroundDisabled", "RadioButtonOuterEllipseStrokeDisabled", "RadioButtonOuterEllipseCheckedStrokeDisabled", "RadioButtonCheckGlyphFillDisabled", "CheckBoxForegroundUncheckedDisabled", "CheckBoxForegroundCheckedDisabled", "CheckBoxForegroundIndeterminateDisabled", "CheckBoxCheckBackgroundStrokeUncheckedDisabled", "CheckBoxCheckBackgroundStrokeCheckedDisabled", "CheckBoxCheckBackgroundStrokeIndeterminateDisabled", "CheckBoxCheckGlyphForegroundCheckedDisabled", "CheckBoxCheckGlyphForegroundIndeterminateDisabled", "HyperlinkButtonForegroundDisabled", "RepeatButtonForegroundDisabled", "ToggleSwitchContentForegroundDisabled", "ToggleSwitchHeaderForegroundDisabled", "ToggleSwitchStrokeOffDisabled", "ToggleSwitchStrokeOnDisabled", "ToggleSwitchKnobFillOffDisabled", "ToggleButtonForegroundDisabled", "ToggleButtonForegroundCheckedDisabled", "ToggleButtonForegroundIndeterminateDisabled", "ComboBoxItemForegroundDisabled", "ComboBoxItemForegroundSelectedDisabled", "ComboBoxForegroundDisabled", "ComboBoxDropDownGlyphForegroundDisabled", "AppBarEllipsisButtonForegroundDisabled", "AccentButtonForegroundDisabled", "CalendarDatePickerForegroundDisabled", "CalendarDatePickerCalendarGlyphForegroundDisabled", "CalendarDatePickerTextForegroundDisabled", "CalendarDatePickerHeaderForegroundDisabled", "CalendarViewBlackoutForeground", "CalendarViewWeekDayForegroundDisabled", "CalendarViewNavigationButtonForegroundDisabled", "HubSectionHeaderButtonForegroundDisabled", "DatePickerHeaderForegroundDisabled", "DatePickerButtonForegroundDisabled", "TimePickerHeaderForegroundDisabled", "TimePickerButtonForegroundDisabled", "TextControlHeaderForegroundDisabled", "ToggleMenuFlyoutItemForegroundDisabled", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "ToggleMenuFlyoutItemCheckGlyphForegroundDisabled", "PivotHeaderItemForegroundDisabled", "JumpListDefaultDisabledForeground", "MenuFlyoutItemForegroundDisabled", "MenuFlyoutSubItemForegroundDisabled", "MenuFlyoutSubItemChevronDisabled", "NavigationViewItemForegroundDisabled", "NavigationViewItemForegroundCheckedDisabled", "NavigationViewItemForegroundSelectedDisabled", "TopNavigationViewItemForegroundDisabled", "AppBarButtonForegroundDisabled", "AppBarToggleButtonForegroundDisabled", "AppBarToggleButtonCheckGlyphForegroundDisabled", "AppBarToggleButtonCheckGlyphForegroundCheckedDisabled", "AppBarToggleButtonOverflowLabelForegroundDisabled", "AppBarToggleButtonOverflowLabelForegroundCheckedDisabled", "CommandBarEllipsisIconForegroundDisabled", "TreeViewItemBackgroundSelectedDisabled", "TreeViewItemForegroundDisabled", "TreeViewItemForegroundSelectedDisabled", "SplitButtonForegroundDisabled", "SplitButtonForegroundCheckedDisabled", "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "AppBarButtonKeyboardAcceleratorTextForegroundDisabled", "AppBarToggleButtonKeyboardAcceleratorTextForegroundDisabled", "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedDisabled", "AppBarButtonSubItemChevronForegroundDisabled" } },
            { "SystemControlBackgroundAltHighBrush", new[] { "SliderInlineTickBarFill", "CalendarViewCalendarItemBackground", "CalendarViewBackground" } },
            { "SystemControlBackgroundBaseLowBrush", new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundDisabled", "RepeatButtonBackground", "RepeatButtonBackgroundPointerOver", "RepeatButtonBackgroundDisabled", "ThumbBackground", "ToggleButtonBackground", "ToggleButtonBackgroundPointerOver", "ToggleButtonBackgroundDisabled", "ToggleButtonBackgroundCheckedDisabled", "ToggleButtonBackgroundIndeterminate", "ToggleButtonBackgroundIndeterminatePointerOver", "ToggleButtonBackgroundIndeterminateDisabled", "ComboBoxBackgroundDisabled", "ContentDialogBorderBrush", "AccentButtonBackgroundDisabled", "CalendarDatePickerBackgroundPressed", "CalendarDatePickerBackgroundDisabled", "DatePickerButtonBackgroundPressed", "DatePickerButtonBackgroundDisabled", "TimePickerButtonBackgroundPressed", "TimePickerButtonBackgroundDisabled", "TextControlBackgroundDisabled", "JumpListDefaultDisabledBackground", "RatingControlUnselectedForeground", "SwipeItemBackground", "SwipeItemPreThresholdExecuteBackground", "SplitButtonBackground", "SplitButtonBackgroundPointerOver", "SplitButtonBackgroundDisabled", "SplitButtonBackgroundCheckedDisabled" } },
            { "SystemControlBackgroundBaseMediumLowBrush", new[] { "ButtonBackgroundPressed", "RepeatButtonBackgroundPressed", "ToggleButtonBackgroundPressed", "ToggleButtonBackgroundIndeterminatePressed", "ScrollBarThumbFillPointerOver", "AccentButtonBackgroundPressed", "FlipViewNextPreviousButtonBackground", "PivotNextButtonBackground", "PivotPreviousButtonBackground", "AppBarToggleButtonForegroundCheckedDisabled", "SwipeItemBackgroundPressed", "SplitButtonBackgroundPressed" } },
            { "SystemControlHighlightBaseHighBrush", new[] { "ButtonForegroundPointerOver", "ButtonForegroundPressed", "RadioButtonOuterEllipseStrokePointerOver", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "RepeatButtonForegroundPointerOver", "RepeatButtonForegroundPressed", "ToggleSwitchStrokeOffPointerOver", "ToggleSwitchStrokeOn", "ToggleSwitchKnobFillOffPointerOver", "ToggleButtonForegroundPointerOver", "ToggleButtonForegroundPressed", "ToggleButtonForegroundIndeterminatePointerOver", "ToggleButtonForegroundIndeterminatePressed", "AccentButtonForegroundPressed", "CalendarViewSelectedForeground", "CalendarViewPressedForeground", "DatePickerButtonForegroundPointerOver", "DatePickerButtonForegroundPressed", "TimePickerButtonForegroundPointerOver", "TimePickerButtonForegroundPressed", "SplitButtonForegroundPointerOver", "SplitButtonForegroundPressed" } },
            { "SystemControlForegroundTransparentBrush", new[] { "ButtonBorderBrush", "RepeatButtonBorderBrush", "ToggleButtonBorderBrush", "ToggleButtonBorderBrushIndeterminate", "ScrollBarTrackStroke", "ScrollBarTrackStrokePointerOver", "AppBarHighContrastBorder", "AccentButtonBorderBrush", "FlipViewNextPreviousButtonBorderBrush", "FlipViewNextPreviousButtonBorderBrushPointerOver", "FlipViewNextPreviousButtonBorderBrushPressed", "DateTimePickerFlyoutButtonBorderBrush", "PivotNextButtonBorderBrush", "PivotNextButtonBorderBrushPointerOver", "PivotNextButtonBorderBrushPressed", "PivotPreviousButtonBorderBrush", "PivotPreviousButtonBorderBrushPointerOver", "PivotPreviousButtonBorderBrushPressed", "KeyTipBorderBrush", "CommandBarHighContrastBorder", "SplitButtonBorderBrush" } },
            { "SystemControlHighlightBaseMediumLowBrush", new[] { "ButtonBorderBrushPointerOver", "HyperlinkButtonForegroundPressed", "RepeatButtonBorderBrushPointerOver", "ThumbBackgroundPointerOver", "ToggleButtonBackgroundCheckedPressed", "ToggleButtonBorderBrushPointerOver", "ToggleButtonBorderBrushCheckedPointerOver", "ToggleButtonBorderBrushIndeterminatePointerOver", "ComboBoxBackgroundBorderBrushUnfocused", "ComboBoxBorderBrushPressed", "AccentButtonBorderBrushPointerOver", "CalendarDatePickerBorderBrushPressed", "CalendarViewHoverBorderBrush", "HubSectionHeaderButtonForegroundPressed", "DatePickerButtonBorderBrushPressed", "TimePickerButtonBorderBrushPressed", "MenuBarItemBorderBrushPressed", "MenuBarItemBorderBrushSelected", "SplitButtonBackgroundCheckedPressed", "SplitButtonBorderBrushPointerOver", "SplitButtonBorderBrushCheckedPointerOver" } },
            { "SystemControlHighlightTransparentBrush", new[] { "ButtonBorderBrushPressed", "RadioButtonOuterEllipseCheckedFillPointerOver", "RadioButtonOuterEllipseCheckedFillPressed", "CheckBoxCheckBackgroundStrokeUncheckedPressed", "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPressed", "CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckBackgroundFillIndeterminatePointerOver", "CheckBoxCheckBackgroundFillIndeterminatePressed", "RepeatButtonBorderBrushPressed", "ThumbBorderBrush", "ThumbBorderBrushPointerOver", "ThumbBorderBrushPressed", "ToggleButtonBorderBrushPressed", "ToggleButtonBorderBrushIndeterminatePressed", "ComboBoxBackgroundBorderBrushFocused", "AccentButtonBorderBrushPressed", "CalendarViewNavigationButtonBorderBrushPointerOver", "DateTimePickerFlyoutButtonBorderBrushPointerOver", "DateTimePickerFlyoutButtonBorderBrushPressed", "PivotHeaderItemBackgroundUnselectedPointerOver", "PivotHeaderItemBackgroundUnselectedPressed", "PivotHeaderItemBackgroundSelected", "PivotHeaderItemBackgroundSelectedPointerOver", "PivotHeaderItemBackgroundSelectedPressed", "SplitButtonBorderBrushPressed" } },
            { "SystemControlDisabledTransparentBrush", new[] { "ButtonBorderBrushDisabled", "RepeatButtonBorderBrushDisabled", "ToggleButtonBorderBrushDisabled", "ToggleButtonBorderBrushCheckedDisabled", "ToggleButtonBorderBrushIndeterminateDisabled", "ScrollBarThumbFillDisabled", "ScrollBarTrackFillDisabled", "ScrollBarTrackStrokeDisabled", "AccentButtonBorderBrushDisabled", "SplitButtonBorderBrushDisabled", "SplitButtonBorderBrushCheckedDisabled" } },
            { "SystemControlForegroundBaseMediumHighBrush", new[] { "RadioButtonOuterEllipseStroke", "CheckBoxCheckBackgroundStrokeUnchecked", "CheckBoxCheckGlyphForegroundIndeterminate", "ToggleSwitchStrokeOff", "ToggleSwitchStrokeOffPressed", "ToggleSwitchKnobFillOff", "ComboBoxDropDownGlyphForeground", "CalendarDatePickerCalendarGlyphForeground", "ToggleMenuFlyoutItemCheckGlyphForeground", "GridViewItemCheckBrush", "MenuFlyoutSubItemChevron", "TopNavigationViewItemForegroundPointerOver", "TopNavigationViewItemForegroundPressed", "ListViewItemCheckBrush", "ListViewItemCheckBoxBrush", "TreeViewItemCheckBoxBorderSelected", "TreeViewItemCheckGlyphSelected", "AppBarButtonSubItemChevronForeground" } },
            { "SystemControlHighlightBaseMediumBrush", new[] { "RadioButtonOuterEllipseStrokePressed", "RadioButtonOuterEllipseCheckedStrokePressed", "CheckBoxCheckBackgroundStrokeIndeterminatePressed", "CheckBoxCheckBackgroundFillCheckedPressed", "ToggleSwitchFillOffPressed", "ToggleSwitchFillOnPressed", "ToggleSwitchStrokeOnPressed", "ThumbBackgroundPressed", "ComboBoxBorderBrushPointerOver", "CalendarDatePickerBorderBrushPointerOver", "CalendarViewPressedBorderBrush", "FlipViewNextPreviousButtonBackgroundPointerOver", "DatePickerButtonBorderBrushPointerOver", "TimePickerButtonBorderBrushPointerOver", "TextControlBorderBrushPointerOver", "PivotNextButtonBackgroundPointerOver", "PivotPreviousButtonBackgroundPointerOver", "MenuBarItemBorderBrushPointerOver" } },
            { "SystemControlHighlightAltTransparentBrush", new[] { "RadioButtonOuterEllipseCheckedFill", "ToggleButtonBorderBrushChecked", "SplitButtonBorderBrushChecked", "SplitButtonBorderBrushCheckedPressed" } },
            { "SystemControlHighlightBaseMediumHighBrush", new[] { "RadioButtonCheckGlyphFill", "FlipViewNextPreviousButtonBackgroundPressed", "PivotNextButtonBackgroundPressed", "PivotPreviousButtonBackgroundPressed" } },
            { "SystemControlHighlightAltBaseHighBrush", new[] { "RadioButtonCheckGlyphFillPointerOver", "ComboBoxItemForegroundPressed", "ComboBoxItemForegroundPointerOver", "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedUnfocused", "ComboBoxItemForegroundSelectedPressed", "ComboBoxItemForegroundSelectedPointerOver", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed", "ComboBoxPlaceHolderForegroundFocusedPressed", "AppBarEllipsisButtonForegroundPointerOver", "DateTimePickerFlyoutButtonForegroundPointerOver", "DateTimePickerFlyoutButtonForegroundPressed", "DatePickerButtonForegroundFocused", "TimePickerButtonForegroundFocused", "LoopingSelectorItemForegroundSelected", "LoopingSelectorItemForegroundPointerOver", "LoopingSelectorItemForegroundPressed", "ToggleMenuFlyoutItemForegroundPointerOver", "ToggleMenuFlyoutItemForegroundPressed", "ToggleMenuFlyoutItemCheckGlyphForegroundPointerOver", "ToggleMenuFlyoutItemCheckGlyphForegroundPressed", "PivotHeaderItemForegroundSelected", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed", "MenuFlyoutSubItemForegroundPointerOver", "MenuFlyoutSubItemForegroundPressed", "MenuFlyoutSubItemForegroundSubMenuOpened", "MenuFlyoutSubItemChevronPointerOver", "MenuFlyoutSubItemChevronPressed", "MenuFlyoutSubItemChevronSubMenuOpened", "NavigationViewItemForegroundPointerOver", "NavigationViewItemForegroundPressed", "NavigationViewItemForegroundChecked", "NavigationViewItemForegroundCheckedPointerOver", "NavigationViewItemForegroundCheckedPressed", "NavigationViewItemForegroundSelected", "NavigationViewItemForegroundSelectedPointerOver", "NavigationViewItemForegroundSelectedPressed", "AppBarButtonForegroundPointerOver", "AppBarButtonForegroundPressed", "AppBarToggleButtonForegroundPointerOver", "AppBarToggleButtonForegroundPressed", "AppBarToggleButtonForegroundChecked", "AppBarToggleButtonForegroundCheckedPointerOver", "AppBarToggleButtonForegroundCheckedPressed", "AppBarToggleButtonCheckGlyphForegroundPointerOver", "AppBarToggleButtonCheckGlyphForegroundPressed", "AppBarToggleButtonCheckGlyphForegroundCheckedPointerOver", "AppBarToggleButtonCheckGlyphForegroundCheckedPressed", "AppBarToggleButtonOverflowLabelForegroundPointerOver", "AppBarToggleButtonOverflowLabelForegroundPressed", "AppBarToggleButtonOverflowLabelForegroundCheckedPointerOver", "AppBarToggleButtonOverflowLabelForegroundCheckedPressed", "ListViewItemForegroundPointerOver", "ListViewItemForegroundSelected", "TreeViewItemForegroundPointerOver", "TreeViewItemForegroundPressed", "TreeViewItemForegroundSelected", "TreeViewItemForegroundSelectedPointerOver", "TreeViewItemForegroundSelectedPressed", "AppBarButtonForegroundSubMenuOpened", "AppBarButtonSubItemChevronForegroundPointerOver", "AppBarButtonSubItemChevronForegroundPressed", "AppBarButtonSubItemChevronForegroundSubMenuOpened" } },
            { "SystemControlHighlightAltBaseMediumBrush", new[] { "RadioButtonCheckGlyphFillPressed", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", "AppBarButtonKeyboardAcceleratorTextForegroundPointerOver", "AppBarButtonKeyboardAcceleratorTextForegroundPressed", "AppBarToggleButtonKeyboardAcceleratorTextForegroundPointerOver", "AppBarToggleButtonKeyboardAcceleratorTextForegroundPressed", "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedPointerOver", "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedPressed", "AppBarButtonKeyboardAcceleratorTextForegroundSubMenuOpened" } },
            { "SystemControlBackgroundBaseMediumBrush", new[] { "CheckBoxCheckBackgroundFillUncheckedPressed", "ScrollBarButtonBackgroundPressed", "ScrollBarThumbFillPressed", "SwipeItemPreThresholdExecuteForeground" } },
            { "SystemControlBackgroundAccentBrush", new[] { "CheckBoxCheckBackgroundFillCheckedPointerOver", "JumpListDefaultEnabledBackground", "SwipeItemPostThresholdExecuteBackground" } },
            { "SystemControlHighlightAltChromeWhiteBrush", new[] { "CheckBoxCheckGlyphForegroundUnchecked", "CheckBoxCheckGlyphForegroundUncheckedPointerOver", "CheckBoxCheckGlyphForegroundUncheckedPressed", "CheckBoxCheckGlyphForegroundUncheckedDisabled", "CheckBoxCheckGlyphForegroundChecked", "ToggleSwitchKnobFillOffPressed", "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPressed", "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed", "CalendarViewTodayForeground", "TextControlButtonForegroundPressed", "GridViewItemDragForeground", "ListViewItemDragForeground", "SplitButtonForegroundChecked", "SplitButtonForegroundCheckedPointerOver", "SplitButtonForegroundCheckedPressed" } },
            { "SystemControlForegroundChromeWhiteBrush", new[] { "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed", "JumpListDefaultEnabledForeground", "SwipeItemPostThresholdExecuteForeground" } },
            { "SystemControlHyperlinkTextBrush", new[] { "HyperlinkButtonForeground", "HubSectionHeaderButtonForeground", "ContentLinkForegroundColor" } },
            { "SystemControlPageBackgroundTransparentBrush", new[] { "HyperlinkButtonBackground", "HyperlinkButtonBackgroundPointerOver", "HyperlinkButtonBackgroundPressed", "HyperlinkButtonBackgroundDisabled" } },
            { "SystemControlHighlightAltListAccentHighBrush", new[] { "ToggleSwitchFillOnPointerOver" } },
            { "SystemControlDisabledBaseLowBrush", new[] { "ToggleSwitchFillOnDisabled", "ComboBoxBorderBrushDisabled", "CalendarDatePickerBorderBrushDisabled", "DatePickerSpacerFillDisabled", "DatePickerButtonBorderBrushDisabled", "TimePickerSpacerFillDisabled", "TimePickerButtonBorderBrushDisabled", "TextControlBorderBrushDisabled", "ColorPickerSliderTrackFillDisabled" } },
            { "SystemControlHighlightListAccentHighBrush", new[] { "ToggleSwitchStrokeOnPointerOver", "ComboBoxItemBackgroundSelectedPressed", "CalendarViewSelectedPressedBorderBrush", "GridViewItemBackgroundSelectedPressed", "MenuFlyoutSubItemBackgroundPressed", "ListViewItemBackgroundSelectedPressed" } },
            { "SystemControlHighlightChromeWhiteBrush", new[] { "ToggleSwitchKnobFillOnPointerOver" } },
            { "SystemControlPageBackgroundBaseLowBrush", new[] { "ToggleSwitchKnobFillOnDisabled" } },
            { "SystemControlBackgroundListLowBrush", new[] { "ScrollBarButtonBackgroundPointerOver", "ComboBoxDropDownBackgroundPointerOver", "MenuBarItemBackgroundPointerOver" } },
            { "SystemControlForegroundAltHighBrush", new[] { "ScrollBarButtonArrowForegroundPressed", "TextControlHighlighterForeground", "GridViewItemFocusBorderBrush", "ListViewItemFocusBorderBrush" } },
            { "SystemControlForegroundBaseLowBrush", new[] { "ScrollBarButtonArrowForegroundDisabled", "ListViewHeaderItemDividerStroke", "DatePickerSpacerFill", "DatePickerFlyoutPresenterSpacerFill", "TimePickerSpacerFill", "TimePickerFlyoutPresenterSpacerFill", "GridViewHeaderItemDividerStroke" } },
            { "SystemControlForegroundChromeDisabledLowBrush", new[] { "ScrollBarThumbFill" } },
            { "SystemControlDisabledChromeHighBrush", new[] { "ScrollBarPanningThumbBackgroundDisabled" } },
            { "SystemBaseLowColor", new[] { "ScrollBarThumbBackgroundColor" } },
            { "SystemChromeDisabledLowColor", new[] { "ScrollBarPanningThumbBackgroundColor" } },
            { "SystemControlHighlightListMediumBrush", new[] { "ComboBoxItemBackgroundPressed", "AppBarEllipsisButtonBackgroundPressed", "DateTimePickerFlyoutButtonBackgroundPressed", "LoopingSelectorItemBackgroundPressed", "ToggleMenuFlyoutItemBackgroundPressed", "GridViewItemBackgroundPressed", "MenuFlyoutItemBackgroundPressed", "AppBarButtonBackgroundPressed", "AppBarToggleButtonBackgroundHighLightOverlayPressed", "AppBarToggleButtonBackgroundHighLightOverlayCheckedPressed", "ListViewItemBackgroundPressed" } },
            { "SystemControlHighlightListLowBrush", new[] { "ComboBoxItemBackgroundPointerOver", "AppBarEllipsisButtonBackgroundPointerOver", "DateTimePickerFlyoutButtonBackgroundPointerOver", "LoopingSelectorItemBackgroundPointerOver", "ToggleMenuFlyoutItemBackgroundPointerOver", "GridViewItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutSubItemBackgroundPointerOver", "AppBarButtonBackgroundPointerOver", "AppBarToggleButtonBackgroundHighLightOverlayPointerOver", "AppBarToggleButtonBackgroundHighLightOverlayCheckedPointerOver", "ListViewItemBackgroundPointerOver" } },
            { "SystemControlHighlightListAccentLowBrush", new[] { "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedUnfocused", "ComboBoxBackgroundUnfocused", "CalendarDatePickerBackgroundFocused", "DatePickerButtonBackgroundFocused", "DatePickerFlyoutPresenterHighlightFill", "TimePickerButtonBackgroundFocused", "TimePickerFlyoutPresenterHighlightFill", "MenuFlyoutSubItemBackgroundSubMenuOpened", "ListViewItemBackgroundSelected", "AppBarButtonBackgroundSubMenuOpened" } },
            { "SystemControlHighlightListAccentMediumBrush", new[] { "ComboBoxItemBackgroundSelectedPointerOver", "CalendarViewSelectedHoverBorderBrush", "GridViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPointerOver" } },
            { "SystemControlBackgroundAltMediumLowBrush", new[] { "ComboBoxBackground", "CalendarDatePickerBackground", "DatePickerButtonBackground", "TimePickerButtonBackground", "TextControlBackground" } },
            { "SystemControlPageBackgroundAltMediumBrush", new[] { "ComboBoxBackgroundPointerOver", "CalendarDatePickerBackgroundPointerOver", "DatePickerButtonBackgroundPointerOver", "TimePickerButtonBackgroundPointerOver", "MediaTransportControlsPanelBackground" } },
            { "SystemControlBackgroundListMediumBrush", new[] { "ComboBoxBackgroundPressed", "ComboBoxDropDownBackgroundPointerPressed", "MenuBarItemBackgroundPressed", "MenuBarItemBackgroundSelected" } },
            { "SystemControlPageTextBaseHighBrush", new[] { "ComboBoxPlaceHolderForeground", "ContentDialogForeground", "HubForeground", "HubSectionHeaderForeground" } },
            { "SystemControlBackgroundChromeBlackLowBrush", new[] { "ComboBoxFocusedDropDownBackgroundPointerOver" } },
            { "SystemControlBackgroundChromeBlackMediumLowBrush", new[] { "ComboBoxFocusedDropDownBackgroundPointerPressed" } },
            { "SystemControlForegroundAltMediumHighBrush", new[] { "ComboBoxEditableDropDownGlyphForeground", "FlipViewNextPreviousArrowForeground", "PivotNextButtonForeground", "PivotPreviousButtonForeground" } },
            { "SystemControlHighlightAltBaseMediumHighBrush", new[] { "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed", "PivotHeaderItemForegroundUnselectedPointerOver", "PivotHeaderItemForegroundUnselectedPressed", "PivotHeaderItemForegroundSelectedPointerOver", "PivotHeaderItemForegroundSelectedPressed" } },
            { "SystemControlTransientBackgroundBrush", new[] { "ComboBoxDropDownBackground", "DatePickerFlyoutPresenterBackground", "TimePickerFlyoutPresenterBackground", "FlyoutPresenterBackground", "MediaTransportControlsFlyoutBackground", "MenuFlyoutPresenterBackground", "CommandBarOverflowPresenterBackground", "AutoSuggestBoxSuggestionsListBackground" } },
            { "SystemControlTransientBorderBrush", new[] { "ComboBoxDropDownBorderBrush", "ToolTipBorderBrush", "DatePickerFlyoutPresenterBorderBrush", "TimePickerFlyoutPresenterBorderBrush", "FlyoutBorderThemeBrush", "MenuFlyoutPresenterBorderBrush", "CommandBarOverflowPresenterBorderBrush", "AutoSuggestBoxSuggestionsListBorderBrush" } },
            { "SystemControlBackgroundChromeMediumBrush", new[] { "AppBarBackground", "LoopingSelectorButtonBackground", "GridViewItemCheckBoxBrush", "CommandBarBackground" } },
            { "SystemControlPageBackgroundAltHighBrush", new[] { "ContentDialogBackground" } },
            { "SystemControlBackgroundChromeWhiteBrush", new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "TextControlBackgroundFocused", "KeyTipForeground" } },
            { "SystemControlBackgroundChromeMediumLowBrush", new[] { "ToolTipBackground" } },
            { "SystemControlHyperlinkBaseHighBrush", new[] { "CalendarViewOutOfScopeForeground" } },
            { "SystemControlDisabledChromeMediumLowBrush", new[] { "CalendarViewOutOfScopeBackground" } },
            { "SystemControlHyperlinkBaseMediumHighBrush", new[] { "CalendarViewForeground" } },
            { "SystemControlForegroundChromeMediumBrush", new[] { "CalendarViewBorderBrush" } },
            { "SystemControlHyperlinkBaseMediumBrush", new[] { "HubSectionHeaderButtonForegroundPointerOver" } },
            { "SystemControlPageBackgroundListLowBrush", new[] { "FlipViewBackground" } },
            { "SystemControlHighlightAltAltMediumHighBrush", new[] { "FlipViewNextPreviousArrowForegroundPointerOver", "FlipViewNextPreviousArrowForegroundPressed", "PivotNextButtonForegroundPointerOver", "PivotNextButtonForegroundPressed", "PivotPreviousButtonForegroundPointerOver", "PivotPreviousButtonForegroundPressed" } },
            { "SystemControlForegroundChromeBlackHighBrush", new[] { "TextControlForegroundFocused" } },
            { "SystemControlDisabledChromeDisabledLowBrush", new[] { "TextControlForegroundDisabled", "TextControlPlaceholderForegroundDisabled" } },
            { "SystemControlBackgroundAltMediumBrush", new[] { "TextControlBackgroundPointerOver" } },
            { "SystemControlPageTextChromeBlackMediumLowBrush", new[] { "TextControlPlaceholderForegroundFocused" } },
            { "SystemControlForegroundChromeBlackMediumBrush", new[] { "TextControlButtonForeground" } },
            { "SystemControlHighlightAltAccentBrush", new[] { "PivotHeaderItemFocusPipeFill", "PivotHeaderItemSelectedPipeFill" } },
            { "SystemControlFocusVisualPrimaryBrush", new[] { "GridViewItemFocusVisualPrimaryBrush", "ListViewItemFocusVisualPrimaryBrush" } },
            { "SystemControlFocusVisualSecondaryBrush", new[] { "GridViewItemFocusVisualSecondaryBrush", "ListViewItemFocusVisualSecondaryBrush" } },
            { "SystemControlPageBackgroundMediumAltMediumBrush", new[] { "AppBarLightDismissOverlayBackground", "CalendarDatePickerLightDismissOverlayBackground", "ComboBoxLightDismissOverlayBackground", "DatePickerLightDismissOverlayBackground", "FlyoutLightDismissOverlayBackground", "PopupLightDismissOverlayBackground", "SplitViewLightDismissOverlayBackground", "TimePickerLightDismissOverlayBackground", "MenuFlyoutLightDismissOverlayBackground", "CommandBarLightDismissOverlayBackground", "AutoSuggestBoxLightDismissOverlayBackground" } },
            { "SystemControlForegroundChromeGrayBrush", new[] { "KeyTipBackground" } },
            { "SystemBaseMediumLowColor", new[] { "RatingControlDisabledSelectedForeground" } },
            { "SystemControlForegroundChromeHighBrush", new[] { "ColorPickerSliderThumbBackgroundPressed" } },
            { "SystemControlDisabledAccentBrush", new[] { "AppBarToggleButtonBackgroundCheckedDisabled" } },
        };



        private readonly Dictionary<string, object> _defaultDark = new Dictionary<string, object>
        {
            { "ApplicationPageBackgroundThemeBrush", "SystemChromeLowColor" },
            { "PageHeaderForegroundBrush", "SystemBaseHighColor" },
            { "PageHeaderHighlightBrush", "SystemAccentColor" },
            { "PageHeaderDisabledBrush", "SystemControlForegroundBaseMediumBrush" },
            { "PageHeaderBackgroundBrush", "SystemControlBackgroundChromeMediumLowBrush" },
            { "PageSubHeaderBackgroundBrush", "SystemControlBackgroundChromeMediumBrush" },
            { "TelegramSeparatorMediumBrush", "SystemControlBackgroundChromeMediumBrush" },
            { "ChatOnlineBadgeBrush", Color.FromArgb(0xFF, 0x89, 0xDF, 0x9E) },
            { "ChatVerifiedBadgeBrush", "SystemAccentColor" },
            { "ChatLastMessageStateBrush", "SystemAccentColor" },
            { "ChatFromLabelBrush", "SystemAccentColor" },
            { "ChatDraftLabelBrush", Color.FromArgb(0xFF, 0xDD, 0x4B, 0x39) },
            { "ChatUnreadBadgeMutedBrush", Color.FromArgb(0xFF, 0x44, 0x44, 0x44) },
            { "ChatUnreadLabelMutedBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "ChatFailedBadgeBrush", Color.FromArgb(0xFF, 0xFF, 0x00, 0x00) },
            { "ChatFailedLabelBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ChatUnreadBadgeBrush", "SystemAccentColor" },
            { "ChatUnreadLabelBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "MessageForegroundColor", Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5) },
            { "MessageForegroundOutColor", Color.FromArgb(0xFF, 0xE4, 0xEC, 0xF2) },
            { "MessageForegroundLinkColor", Color.FromArgb(0xFF, 0x70, 0xBA, 0xF5) },
            { "MessageForegroundLinkOutColor", Color.FromArgb(0xFF, 0x83, 0xCA, 0xFF) },
            { "MessageBackgroundColor", Color.FromArgb(0xFF, 0x18, 0x25, 0x33) },
            { "MessageBackgroundOutColor", Color.FromArgb(0xFF, 0x2B, 0x52, 0x78) },
            { "MessageSubtleLabelColor", Color.FromArgb(0xFF, 0x6D, 0x7F, 0x8F) },
            { "MessageSubtleLabelOutColor", Color.FromArgb(0xFF, 0x7D, 0xA8, 0xD3) },
            { "MessageSubtleGlyphColor", Color.FromArgb(0xFF, 0x6D, 0x7F, 0x8F) },
            { "MessageSubtleGlyphOutColor", Color.FromArgb(0xFF, 0x72, 0xBC, 0xFD) },
            { "MessageSubtleForegroundColor", Color.FromArgb(0xFF, 0x6D, 0x7F, 0x8F) },
            { "MessageSubtleForegroundOutColor", Color.FromArgb(0xFF, 0x7D, 0xA8, 0xD3) },
            { "MessageHeaderForegroundColor", Color.FromArgb(0xFF, 0x71, 0xBA, 0xFA) },
            { "MessageHeaderForegroundOutColor", Color.FromArgb(0xFF, 0x90, 0xCA, 0xFF) },
            { "MessageHeaderBorderColor", Color.FromArgb(0xFF, 0x42, 0x9B, 0xDB) },
            { "MessageHeaderBorderOutColor", Color.FromArgb(0xFF, 0x65, 0xB9, 0xF4) },
            { "MessageMediaForegroundColor", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "MessageMediaForegroundOutColor", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "MessageMediaBackgroundColor", Color.FromArgb(0xFF, 0x3F, 0x96, 0xD0) },
            { "MessageMediaBackgroundOutColor", Color.FromArgb(0xFF, 0x4C, 0x9C, 0xE2) },
            { "MessageOverlayBackgroundColor", Color.FromArgb(0x54, 0x00, 0x00, 0x00) },
            { "MessageOverlayBackgroundOutColor", Color.FromArgb(0x54, 0x00, 0x00, 0x00) },
            { "MessageCallForegroundColor", Color.FromArgb(0xFF, 0x49, 0xA2, 0xF0) },
            { "MessageCallForegroundOutColor", Color.FromArgb(0xFF, 0x49, 0xA2, 0xF0) },
            { "MessageCallMissedForegroundColor", Color.FromArgb(0xFF, 0xED, 0x50, 0x50) },
            { "MessageCallMissedForegroundOutColor", Color.FromArgb(0xFF, 0xED, 0x50, 0x50) },
            { "SystemAltHighColor", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "SystemAltLowColor", Color.FromArgb(0x33, 0x00, 0x00, 0x00) },
            { "SystemAltMediumColor", Color.FromArgb(0x99, 0x00, 0x00, 0x00) },
            { "SystemAltMediumHighColor", Color.FromArgb(0xCC, 0x00, 0x00, 0x00) },
            { "SystemAltMediumLowColor", Color.FromArgb(0x66, 0x00, 0x00, 0x00) },
            { "SystemBaseHighColor", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "SystemBaseLowColor", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) },
            { "SystemBaseMediumColor", Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) },
            { "SystemBaseMediumHighColor", Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) },
            { "SystemBaseMediumLowColor", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) },
            { "SystemChromeAltLowColor", Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2) },
            { "SystemChromeBlackHighColor", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "SystemChromeBlackLowColor", Color.FromArgb(0x33, 0x00, 0x00, 0x00) },
            { "SystemChromeBlackMediumLowColor", Color.FromArgb(0x66, 0x00, 0x00, 0x00) },
            { "SystemChromeBlackMediumColor", Color.FromArgb(0xCC, 0x00, 0x00, 0x00) },
            { "SystemChromeDisabledHighColor", Color.FromArgb(0xFF, 0x33, 0x33, 0x33) },
            { "SystemChromeDisabledLowColor", Color.FromArgb(0xFF, 0x85, 0x85, 0x85) },
            { "SystemChromeHighColor", Color.FromArgb(0xFF, 0x76, 0x76, 0x76) },
            { "SystemChromeLowColor", Color.FromArgb(0xFF, 0x17, 0x17, 0x17) },
            { "SystemChromeMediumColor", Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F) },
            { "SystemChromeMediumLowColor", Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B) },
            { "SystemChromeWhiteColor", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "SystemChromeGrayColor", Color.FromArgb(0xFF, 0x76, 0x76, 0x76) },
            { "SystemListLowColor", Color.FromArgb(0x19, 0xFF, 0xFF, 0xFF) },
            { "SystemListMediumColor", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) },
            { "SystemControlBackgroundAccentBrush", "SystemAccentColor" },
            { "SystemControlBackgroundAltHighBrush", "SystemAltHighColor" },
            { "SystemControlBackgroundAltMediumHighBrush", "SystemAltMediumHighColor" },
            { "SystemControlBackgroundAltMediumBrush", "SystemAltMediumColor" },
            { "SystemControlBackgroundAltMediumLowBrush", "SystemAltMediumLowColor" },
            { "SystemControlBackgroundBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlBackgroundBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlBackgroundBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlBackgroundBaseMediumHighBrush", "SystemBaseMediumHighColor" },
            { "SystemControlBackgroundBaseMediumLowBrush", "SystemBaseMediumLowColor" },
            { "SystemControlBackgroundChromeBlackHighBrush", "SystemChromeBlackHighColor" },
            { "SystemControlBackgroundChromeBlackMediumBrush", "SystemChromeBlackMediumColor" },
            { "SystemControlBackgroundChromeBlackLowBrush", "SystemChromeBlackLowColor" },
            { "SystemControlBackgroundChromeBlackMediumLowBrush", "SystemChromeBlackMediumLowColor" },
            { "SystemControlBackgroundChromeMediumBrush", "SystemChromeMediumColor" },
            { "SystemControlBackgroundChromeMediumLowBrush", "SystemChromeMediumLowColor" },
            { "SystemControlBackgroundChromeWhiteBrush", "SystemChromeWhiteColor" },
            { "SystemControlBackgroundListLowBrush", "SystemListLowColor" },
            { "SystemControlBackgroundListMediumBrush", "SystemListMediumColor" },
            { "SystemControlDisabledAccentBrush", "SystemAccentColor" },
            { "SystemControlDisabledBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlDisabledBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlDisabledBaseMediumLowBrush", "SystemBaseMediumLowColor" },
            { "SystemControlDisabledChromeDisabledHighBrush", "SystemChromeDisabledHighColor" },
            { "SystemControlDisabledChromeDisabledLowBrush", "SystemChromeDisabledLowColor" },
            { "SystemControlDisabledChromeHighBrush", "SystemChromeHighColor" },
            { "SystemControlDisabledChromeMediumLowBrush", "SystemChromeMediumLowColor" },
            { "SystemControlDisabledListMediumBrush", "SystemListMediumColor" },
            { "SystemControlDisabledTransparentBrush", "Transparent" },
            { "SystemControlFocusVisualPrimaryBrush", "SystemBaseHighColor" },
            { "SystemControlFocusVisualSecondaryBrush", "SystemAltMediumColor" },
            { "SystemControlRevealFocusVisualBrush", "SystemAccentColor" },
            { "SystemControlForegroundAccentBrush", "SystemAccentColor" },
            { "SystemControlForegroundAltHighBrush", "SystemAltHighColor" },
            { "SystemControlForegroundAltMediumHighBrush", "SystemAltMediumHighColor" },
            { "SystemControlForegroundBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlForegroundBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlForegroundBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlForegroundBaseMediumHighBrush", "SystemBaseMediumHighColor" },
            { "SystemControlForegroundBaseMediumLowBrush", "SystemBaseMediumLowColor" },
            { "SystemControlForegroundChromeBlackHighBrush", "SystemChromeBlackHighColor" },
            { "SystemControlForegroundChromeHighBrush", "SystemChromeHighColor" },
            { "SystemControlForegroundChromeMediumBrush", "SystemChromeMediumColor" },
            { "SystemControlForegroundChromeWhiteBrush", "SystemChromeWhiteColor" },
            { "SystemControlForegroundChromeDisabledLowBrush", "SystemChromeDisabledLowColor" },
            { "SystemControlForegroundChromeGrayBrush", "SystemChromeGrayColor" },
            { "SystemControlForegroundListLowBrush", "SystemListLowColor" },
            { "SystemControlForegroundListMediumBrush", "SystemListMediumColor" },
            { "SystemControlForegroundTransparentBrush", "Transparent" },
            { "SystemControlForegroundChromeBlackMediumBrush", "SystemChromeBlackMediumColor" },
            { "SystemControlForegroundChromeBlackMediumLowBrush", "SystemChromeBlackMediumLowColor" },
            { "SystemControlHighlightAccentBrush", "SystemAccentColor" },
            { "SystemControlHighlightAltAccentBrush", "SystemAccentColor" },
            { "SystemControlHighlightAltAltHighBrush", "SystemAltHighColor" },
            { "SystemControlHighlightAltBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlHighlightAltBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlHighlightAltBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlHighlightAltBaseMediumHighBrush", "SystemBaseMediumHighColor" },
            { "SystemControlHighlightAltAltMediumHighBrush", "SystemAltMediumHighColor" },
            { "SystemControlHighlightAltBaseMediumLowBrush", "SystemBaseMediumLowColor" },
            { "SystemControlHighlightAltListAccentHighBrush", "SystemAccentColor" },
            { "SystemControlHighlightAltListAccentLowBrush", "SystemAccentColor" },
            { "SystemControlHighlightAltListAccentMediumBrush", "SystemAccentColor" },
            { "SystemControlHighlightAltChromeWhiteBrush", "SystemChromeWhiteColor" },
            { "SystemControlHighlightAltTransparentBrush", "Transparent" },
            { "SystemControlHighlightBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlHighlightBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlHighlightBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlHighlightBaseMediumHighBrush", "SystemBaseMediumHighColor" },
            { "SystemControlHighlightBaseMediumLowBrush", "SystemBaseMediumLowColor" },
            { "SystemControlHighlightChromeAltLowBrush", "SystemChromeAltLowColor" },
            { "SystemControlHighlightChromeHighBrush", "SystemChromeHighColor" },
            { "SystemControlHighlightListAccentHighBrush", "SystemAccentColor" },
            { "SystemControlHighlightListAccentLowBrush", "SystemAccentColor" },
            { "SystemControlHighlightListAccentMediumBrush", "SystemAccentColor" },
            { "SystemControlHighlightListMediumBrush", "SystemListMediumColor" },
            { "SystemControlHighlightListLowBrush", "SystemListLowColor" },
            { "SystemControlHighlightChromeWhiteBrush", "SystemChromeWhiteColor" },
            { "SystemControlHighlightTransparentBrush", "Transparent" },
            { "SystemControlHyperlinkTextBrush", "SystemAccentColor" },
            { "SystemControlHyperlinkBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlHyperlinkBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlHyperlinkBaseMediumHighBrush", "SystemBaseMediumHighColor" },
            { "SystemControlPageBackgroundAltMediumBrush", "SystemAltMediumColor" },
            { "SystemControlPageBackgroundAltHighBrush", "SystemAltHighColor" },
            { "SystemControlPageBackgroundMediumAltMediumBrush", "SystemAltMediumColor" },
            { "SystemControlPageBackgroundBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlPageBackgroundBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlPageBackgroundListLowBrush", "SystemListLowColor" },
            { "SystemControlPageBackgroundChromeLowBrush", "SystemChromeLowColor" },
            { "SystemControlPageBackgroundChromeMediumLowBrush", "SystemChromeMediumLowColor" },
            { "SystemControlPageBackgroundTransparentBrush", "Transparent" },
            { "SystemControlPageTextBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlPageTextBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlPageTextChromeBlackMediumLowBrush", "SystemChromeBlackMediumLowColor" },
            { "SystemControlTransparentBrush", "Transparent" },
            { "SystemControlErrorTextForegroundBrush", "SystemErrorTextColor" },
            { "SystemControlTransientBorderBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "SystemControlDescriptionTextForegroundBrush", "SystemControlPageTextBaseMediumBrush" },
            { "SliderContainerBackground", "SystemControlTransparentBrush" },
            { "SliderContainerBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "SliderContainerBackgroundPressed", "SystemControlTransparentBrush" },
            { "SliderContainerBackgroundDisabled", "SystemControlTransparentBrush" },
            { "SliderThumbBackground", "SystemControlForegroundAccentBrush" },
            { "SliderThumbBackgroundPointerOver", "SystemControlHighlightChromeAltLowBrush" },
            { "SliderThumbBackgroundPressed", "SystemControlHighlightChromeHighBrush" },
            { "SliderThumbBackgroundDisabled", "SystemControlDisabledChromeDisabledHighBrush" },
            { "SliderTrackFill", "SystemControlForegroundBaseMediumLowBrush" },
            { "SliderTrackFillPointerOver", "SystemControlForegroundBaseMediumBrush" },
            { "SliderTrackFillPressed", "SystemControlForegroundBaseMediumLowBrush" },
            { "SliderTrackFillDisabled", "SystemControlDisabledChromeDisabledHighBrush" },
            { "SliderTrackValueFill", "SystemControlHighlightAccentBrush" },
            { "SliderTrackValueFillPointerOver", "SystemControlHighlightAccentBrush" },
            { "SliderTrackValueFillPressed", "SystemControlHighlightAccentBrush" },
            { "SliderTrackValueFillDisabled", "SystemControlDisabledChromeDisabledHighBrush" },
            { "SliderHeaderForeground", "SystemControlForegroundBaseHighBrush" },
            { "SliderHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "SliderTickBarFill", "SystemControlForegroundBaseMediumLowBrush" },
            { "SliderTickBarFillDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "SliderInlineTickBarFill", "SystemControlBackgroundAltHighBrush" },
            { "ButtonBackground", "SystemControlBackgroundBaseLowBrush" },
            { "ButtonBackgroundPointerOver", "SystemControlBackgroundBaseLowBrush" },
            { "ButtonBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "ButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "ButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "ButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "ButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "ButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "ButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "ButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "ButtonBorderBrushDisabled", "SystemControlDisabledTransparentBrush" },
            { "RadioButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "RadioButtonForegroundPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "RadioButtonForegroundPressed", "SystemControlForegroundBaseHighBrush" },
            { "RadioButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "RadioButtonBackground", "SystemControlTransparentBrush" },
            { "RadioButtonBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "RadioButtonBackgroundPressed", "SystemControlTransparentBrush" },
            { "RadioButtonBackgroundDisabled", "SystemControlTransparentBrush" },
            { "RadioButtonBorderBrush", "SystemControlTransparentBrush" },
            { "RadioButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "RadioButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "RadioButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "RadioButtonOuterEllipseStroke", "SystemControlForegroundBaseMediumHighBrush" },
            { "RadioButtonOuterEllipseStrokePointerOver", "SystemControlHighlightBaseHighBrush" },
            { "RadioButtonOuterEllipseStrokePressed", "SystemControlHighlightBaseMediumBrush" },
            { "RadioButtonOuterEllipseStrokeDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "RadioButtonOuterEllipseFill", "SystemControlTransparentBrush" },
            { "RadioButtonOuterEllipseFillPointerOver", "SystemControlTransparentBrush" },
            { "RadioButtonOuterEllipseFillPressed", "SystemControlTransparentBrush" },
            { "RadioButtonOuterEllipseFillDisabled", "SystemControlTransparentBrush" },
            { "RadioButtonOuterEllipseCheckedStroke", "SystemControlHighlightAccentBrush" },
            { "RadioButtonOuterEllipseCheckedStrokePointerOver", "SystemControlHighlightAccentBrush" },
            { "RadioButtonOuterEllipseCheckedStrokePressed", "SystemControlHighlightBaseMediumBrush" },
            { "RadioButtonOuterEllipseCheckedStrokeDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "RadioButtonOuterEllipseCheckedFill", "SystemControlHighlightAltTransparentBrush" },
            { "RadioButtonOuterEllipseCheckedFillPointerOver", "SystemControlHighlightTransparentBrush" },
            { "RadioButtonOuterEllipseCheckedFillPressed", "SystemControlHighlightTransparentBrush" },
            { "RadioButtonOuterEllipseCheckedFillDisabled", "SystemControlTransparentBrush" },
            { "RadioButtonCheckGlyphFill", "SystemControlHighlightBaseMediumHighBrush" },
            { "RadioButtonCheckGlyphFillPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "RadioButtonCheckGlyphFillPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "RadioButtonCheckGlyphFillDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "RadioButtonCheckGlyphStroke", "SystemControlTransparentBrush" },
            { "RadioButtonCheckGlyphStrokePointerOver", "SystemControlTransparentBrush" },
            { "RadioButtonCheckGlyphStrokePressed", "SystemControlTransparentBrush" },
            { "RadioButtonCheckGlyphStrokeDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxForegroundUnchecked", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundUncheckedPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundUncheckedPressed", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundUncheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxForegroundChecked", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundCheckedPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundCheckedPressed", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxForegroundIndeterminate", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundIndeterminatePointerOver", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundIndeterminatePressed", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundIndeterminateDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxBackgroundUnchecked", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundUncheckedPointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundUncheckedPressed", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundUncheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundChecked", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundCheckedPointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundCheckedPressed", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundCheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundIndeterminate", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundIndeterminatePointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundIndeterminatePressed", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundIndeterminateDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushUnchecked", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushUncheckedPointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushUncheckedPressed", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushUncheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushChecked", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushCheckedPointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushCheckedPressed", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushCheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushIndeterminate", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushIndeterminatePointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushIndeterminatePressed", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushIndeterminateDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxCheckBackgroundStrokeUnchecked", "SystemControlForegroundBaseMediumHighBrush" },
            { "CheckBoxCheckBackgroundStrokeUncheckedPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "CheckBoxCheckBackgroundStrokeUncheckedPressed", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundStrokeUncheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxCheckBackgroundStrokeChecked", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "CheckBoxCheckBackgroundStrokeCheckedPressed", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundStrokeCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxCheckBackgroundStrokeIndeterminate", "SystemControlForegroundAccentBrush" },
            { "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver", "SystemControlHighlightAccentBrush" },
            { "CheckBoxCheckBackgroundStrokeIndeterminatePressed", "SystemControlHighlightBaseMediumBrush" },
            { "CheckBoxCheckBackgroundStrokeIndeterminateDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxCheckBackgroundFillUnchecked", "SystemControlTransparentBrush" },
            { "CheckBoxCheckBackgroundFillUncheckedPointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxCheckBackgroundFillUncheckedPressed", "SystemControlBackgroundBaseMediumBrush" },
            { "CheckBoxCheckBackgroundFillUncheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxCheckBackgroundFillChecked", "SystemControlHighlightAccentBrush" },
            { "CheckBoxCheckBackgroundFillCheckedPointerOver", "SystemControlBackgroundAccentBrush" },
            { "CheckBoxCheckBackgroundFillCheckedPressed", "SystemControlHighlightBaseMediumBrush" },
            { "CheckBoxCheckBackgroundFillCheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxCheckBackgroundFillIndeterminate", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundFillIndeterminatePointerOver", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundFillIndeterminatePressed", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundFillIndeterminateDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxCheckGlyphForegroundUnchecked", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundUncheckedPointerOver", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundUncheckedPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundUncheckedDisabled", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundChecked", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundCheckedPointerOver", "SystemControlForegroundChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundCheckedPressed", "SystemControlForegroundChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxCheckGlyphForegroundIndeterminate", "SystemControlForegroundBaseMediumHighBrush" },
            { "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxCheckGlyphForegroundIndeterminatePressed", "SystemControlForegroundBaseMediumBrush" },
            { "CheckBoxCheckGlyphForegroundIndeterminateDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "HyperlinkButtonForeground", "SystemControlHyperlinkTextBrush" },
            { "HyperlinkButtonForegroundPointerOver", "SystemControlPageTextBaseMediumBrush" },
            { "HyperlinkButtonForegroundPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "HyperlinkButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "HyperlinkButtonBackground", "SystemControlPageBackgroundTransparentBrush" },
            { "HyperlinkButtonBackgroundPointerOver", "SystemControlPageBackgroundTransparentBrush" },
            { "HyperlinkButtonBackgroundPressed", "SystemControlPageBackgroundTransparentBrush" },
            { "HyperlinkButtonBackgroundDisabled", "SystemControlPageBackgroundTransparentBrush" },
            { "HyperlinkButtonBorderBrush", "SystemControlTransparentBrush" },
            { "HyperlinkButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "HyperlinkButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "HyperlinkButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "RepeatButtonBackground", "SystemControlBackgroundBaseLowBrush" },
            { "RepeatButtonBackgroundPointerOver", "SystemControlBackgroundBaseLowBrush" },
            { "RepeatButtonBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "RepeatButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "RepeatButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "RepeatButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "RepeatButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "RepeatButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "RepeatButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "RepeatButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "RepeatButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "RepeatButtonBorderBrushDisabled", "SystemControlDisabledTransparentBrush" },
            { "ToggleSwitchContentForeground", "SystemControlForegroundBaseHighBrush" },
            { "ToggleSwitchContentForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleSwitchHeaderForeground", "SystemControlForegroundBaseHighBrush" },
            { "ToggleSwitchHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleSwitchContainerBackground", "SystemControlTransparentBrush" },
            { "ToggleSwitchContainerBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "ToggleSwitchContainerBackgroundPressed", "SystemControlTransparentBrush" },
            { "ToggleSwitchContainerBackgroundDisabled", "SystemControlTransparentBrush" },
            { "ToggleSwitchFillOff", "SystemControlTransparentBrush" },
            { "ToggleSwitchFillOffPointerOver", "SystemControlTransparentBrush" },
            { "ToggleSwitchFillOffPressed", "SystemControlHighlightBaseMediumBrush" },
            { "ToggleSwitchFillOffDisabled", "SystemControlTransparentBrush" },
            { "ToggleSwitchStrokeOff", "SystemControlForegroundBaseMediumHighBrush" },
            { "ToggleSwitchStrokeOffPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "ToggleSwitchStrokeOffPressed", "SystemControlForegroundBaseMediumHighBrush" },
            { "ToggleSwitchStrokeOffDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleSwitchFillOn", "SystemControlHighlightAccentBrush" },
            { "ToggleSwitchFillOnPointerOver", "SystemControlHighlightAltListAccentHighBrush" },
            { "ToggleSwitchFillOnPressed", "SystemControlHighlightBaseMediumBrush" },
            { "ToggleSwitchFillOnDisabled", "SystemControlDisabledBaseLowBrush" },
            { "ToggleSwitchStrokeOn", "SystemControlHighlightBaseHighBrush" },
            { "ToggleSwitchStrokeOnPointerOver", "SystemControlHighlightListAccentHighBrush" },
            { "ToggleSwitchStrokeOnPressed", "SystemControlHighlightBaseMediumBrush" },
            { "ToggleSwitchStrokeOnDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleSwitchKnobFillOff", "SystemControlForegroundBaseMediumHighBrush" },
            { "ToggleSwitchKnobFillOffPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "ToggleSwitchKnobFillOffPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleSwitchKnobFillOffDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleSwitchKnobFillOn", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleSwitchKnobFillOnPointerOver", "SystemControlHighlightChromeWhiteBrush" },
            { "ToggleSwitchKnobFillOnPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleSwitchKnobFillOnDisabled", "SystemControlPageBackgroundBaseLowBrush" },
            { "ThumbBackground", "SystemControlBackgroundBaseLowBrush" },
            { "ThumbBackgroundPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "ThumbBackgroundPressed", "SystemControlHighlightBaseMediumBrush" },
            { "ThumbBorderBrush", "SystemControlHighlightTransparentBrush" },
            { "ThumbBorderBrushPointerOver", "SystemControlHighlightTransparentBrush" },
            { "ThumbBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "ToggleButtonBackground", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundPointerOver", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "ToggleButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundChecked", "SystemControlHighlightAccentBrush" },
            { "ToggleButtonBackgroundCheckedPointerOver", "SystemControlHighlightAccentBrush" },
            { "ToggleButtonBackgroundCheckedPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "ToggleButtonBackgroundCheckedDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundIndeterminate", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundIndeterminatePointerOver", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundIndeterminatePressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "ToggleButtonBackgroundIndeterminateDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "ToggleButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "ToggleButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "ToggleButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleButtonForegroundChecked", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleButtonForegroundCheckedPointerOver", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleButtonForegroundCheckedPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleButtonForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleButtonForegroundIndeterminate", "SystemControlForegroundBaseHighBrush" },
            { "ToggleButtonForegroundIndeterminatePointerOver", "SystemControlHighlightBaseHighBrush" },
            { "ToggleButtonForegroundIndeterminatePressed", "SystemControlHighlightBaseHighBrush" },
            { "ToggleButtonForegroundIndeterminateDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "ToggleButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "ToggleButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "ToggleButtonBorderBrushDisabled", "SystemControlDisabledTransparentBrush" },
            { "ToggleButtonBorderBrushChecked", "SystemControlHighlightAltTransparentBrush" },
            { "ToggleButtonBorderBrushCheckedPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "ToggleButtonBorderBrushCheckedPressed", "SystemControlTransparentBrush" },
            { "ToggleButtonBorderBrushCheckedDisabled", "SystemControlDisabledTransparentBrush" },
            { "ToggleButtonBorderBrushIndeterminate", "SystemControlForegroundTransparentBrush" },
            { "ToggleButtonBorderBrushIndeterminatePointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "ToggleButtonBorderBrushIndeterminatePressed", "SystemControlHighlightTransparentBrush" },
            { "ToggleButtonBorderBrushIndeterminateDisabled", "SystemControlDisabledTransparentBrush" },
            { "ScrollBarBackground", "SystemControlTransparentBrush" },
            { "ScrollBarBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "ScrollBarBackgroundDisabled", "SystemControlTransparentBrush" },
            { "ScrollBarForeground", "SystemControlTransparentBrush" },
            { "ScrollBarBorderBrush", "SystemControlTransparentBrush" },
            { "ScrollBarBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "ScrollBarBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBackground", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBackgroundPointerOver", "SystemControlBackgroundListLowBrush" },
            { "ScrollBarButtonBackgroundPressed", "SystemControlBackgroundBaseMediumBrush" },
            { "ScrollBarButtonBackgroundDisabled", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBorderBrush", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "ScrollBarButtonArrowForeground", "SystemControlForegroundBaseHighBrush" },
            { "ScrollBarButtonArrowForegroundPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "ScrollBarButtonArrowForegroundPressed", "SystemControlForegroundAltHighBrush" },
            { "ScrollBarButtonArrowForegroundDisabled", "SystemControlForegroundBaseLowBrush" },
            { "ScrollBarThumbFill", "SystemControlForegroundChromeDisabledLowBrush" },
            { "ScrollBarThumbFillPointerOver", "SystemControlBackgroundBaseMediumLowBrush" },
            { "ScrollBarThumbFillPressed", "SystemControlBackgroundBaseMediumBrush" },
            { "ScrollBarThumbFillDisabled", "SystemControlDisabledTransparentBrush" },
            { "ScrollBarTrackFill", "SystemChromeMediumColor" },
            { "ScrollBarTrackFillPointerOver", "SystemChromeMediumColor" },
            { "ScrollBarTrackFillDisabled", "SystemControlDisabledTransparentBrush" },
            { "ScrollBarTrackStroke", "SystemControlForegroundTransparentBrush" },
            { "ScrollBarTrackStrokePointerOver", "SystemControlForegroundTransparentBrush" },
            { "ScrollBarTrackStrokeDisabled", "SystemControlDisabledTransparentBrush" },
            { "ScrollBarPanningThumbBackgroundDisabled", "SystemControlDisabledChromeHighBrush" },
            { "ScrollBarThumbBackgroundColor", "SystemBaseLowColor" },
            { "ScrollBarPanningThumbBackgroundColor", "SystemChromeDisabledLowColor" },
            { "ScrollBarThumbBackground", "ScrollBarThumbBackgroundColor" },
            { "ScrollBarPanningThumbBackground", "ScrollBarPanningThumbBackgroundColor" },
            { "ScrollViewerScrollBarSeparatorBackground", "SystemChromeMediumColor" },
            { "ListViewHeaderItemBackground", "SystemControlTransparentBrush" },
            { "ListViewHeaderItemDividerStroke", "SystemControlForegroundBaseLowBrush" },
            { "ComboBoxItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "ComboBoxItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ComboBoxItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundSelectedUnfocused", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundSelectedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundSelectedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundSelectedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ComboBoxItemBackground", "SystemControlTransparentBrush" },
            { "ComboBoxItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "ComboBoxItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "ComboBoxItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "ComboBoxItemBackgroundSelected", "SystemControlHighlightListAccentLowBrush" },
            { "ComboBoxItemBackgroundSelectedUnfocused", "SystemControlHighlightListAccentLowBrush" },
            { "ComboBoxItemBackgroundSelectedPressed", "SystemControlHighlightListAccentHighBrush" },
            { "ComboBoxItemBackgroundSelectedPointerOver", "SystemControlHighlightListAccentMediumBrush" },
            { "ComboBoxItemBackgroundSelectedDisabled", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrush", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushPressed", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushSelected", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushSelectedUnfocused", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushSelectedPressed", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushSelectedPointerOver", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushSelectedDisabled", "SystemControlTransparentBrush" },
            { "ComboBoxBackground", "SystemControlBackgroundAltMediumLowBrush" },
            { "ComboBoxBackgroundPointerOver", "SystemControlPageBackgroundAltMediumBrush" },
            { "ComboBoxBackgroundPressed", "SystemControlBackgroundListMediumBrush" },
            { "ComboBoxBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "ComboBoxBackgroundUnfocused", "SystemControlHighlightListAccentLowBrush" },
            { "ComboBoxBackgroundBorderBrushFocused", "SystemControlHighlightTransparentBrush" },
            { "ComboBoxBackgroundBorderBrushUnfocused", "SystemControlHighlightBaseMediumLowBrush" },
            { "ComboBoxForeground", "SystemControlForegroundBaseHighBrush" },
            { "ComboBoxForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ComboBoxForegroundFocused", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxForegroundFocusedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxPlaceHolderForeground", "SystemControlPageTextBaseHighBrush" },
            { "ComboBoxPlaceHolderForegroundFocusedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "ComboBoxBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "ComboBoxBorderBrushPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "ComboBoxBorderBrushDisabled", "SystemControlDisabledBaseLowBrush" },
            { "ComboBoxDropDownBackgroundPointerOver", "SystemControlBackgroundListLowBrush" },
            { "ComboBoxDropDownBackgroundPointerPressed", "SystemControlBackgroundListMediumBrush" },
            { "ComboBoxFocusedDropDownBackgroundPointerOver", "SystemControlBackgroundChromeBlackLowBrush" },
            { "ComboBoxFocusedDropDownBackgroundPointerPressed", "SystemControlBackgroundChromeBlackMediumLowBrush" },
            { "ComboBoxDropDownGlyphForeground", "SystemControlForegroundBaseMediumHighBrush" },
            { "ComboBoxEditableDropDownGlyphForeground", "SystemControlForegroundAltMediumHighBrush" },
            { "ComboBoxDropDownGlyphForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ComboBoxDropDownGlyphForegroundFocused", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "ComboBoxDropDownGlyphForegroundFocusedPressed", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "ComboBoxDropDownBackground", "SystemControlTransientBackgroundBrush" },
            { "ComboBoxDropDownForeground", "SystemControlForegroundBaseHighBrush" },
            { "ComboBoxDropDownBorderBrush", "SystemControlTransientBorderBrush" },
            { "AppBarSeparatorForeground", "SystemControlForegroundBaseMediumLowBrush" },
            { "AppBarEllipsisButtonBackground", "SystemControlTransparentBrush" },
            { "AppBarEllipsisButtonBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "AppBarEllipsisButtonBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "AppBarEllipsisButtonBackgroundDisabled", "SystemControlTransparentBrush" },
            { "AppBarEllipsisButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "AppBarEllipsisButtonForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarEllipsisButtonForegroundPressed", "SystemControlForegroundBaseHighBrush" },
            { "AppBarEllipsisButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarEllipsisButtonBorderBrush", "SystemControlTransparentBrush" },
            { "AppBarEllipsisButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "AppBarEllipsisButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "AppBarEllipsisButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "AppBarBackground", "SystemControlBackgroundChromeMediumBrush" },
            { "AppBarForeground", "SystemControlForegroundBaseHighBrush" },
            { "AppBarHighContrastBorder", "SystemControlForegroundTransparentBrush" },
            { "ContentDialogForeground", "SystemControlPageTextBaseHighBrush" },
            { "ContentDialogBackground", "SystemControlPageBackgroundAltHighBrush" },
            { "ContentDialogBorderBrush", "SystemControlBackgroundBaseLowBrush" },
            { "AccentButtonBackground", "SystemControlForegroundAccentBrush" },
            { "AccentButtonBackgroundPointerOver", "SystemControlForegroundAccentBrush" },
            { "AccentButtonBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "AccentButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "AccentButtonForeground", "SystemControlBackgroundChromeWhiteBrush" },
            { "AccentButtonForegroundPointerOver", "SystemControlBackgroundChromeWhiteBrush" },
            { "AccentButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "AccentButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AccentButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "AccentButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "AccentButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "AccentButtonBorderBrushDisabled", "SystemControlDisabledTransparentBrush" },
            { "ToolTipForeground", "SystemControlForegroundBaseHighBrush" },
            { "ToolTipBackground", "SystemControlBackgroundChromeMediumLowBrush" },
            { "ToolTipBorderBrush", "SystemControlTransientBorderBrush" },
            { "CalendarDatePickerForeground", "SystemControlForegroundBaseHighBrush" },
            { "CalendarDatePickerForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarDatePickerCalendarGlyphForeground", "SystemControlForegroundBaseMediumHighBrush" },
            { "CalendarDatePickerCalendarGlyphForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarDatePickerTextForeground", "SystemControlForegroundBaseMediumBrush" },
            { "CalendarDatePickerTextForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarDatePickerTextForegroundSelected", "SystemControlForegroundBaseHighBrush" },
            { "CalendarDatePickerHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarDatePickerBackground", "SystemControlBackgroundAltMediumLowBrush" },
            { "CalendarDatePickerBackgroundPointerOver", "SystemControlPageBackgroundAltMediumBrush" },
            { "CalendarDatePickerBackgroundPressed", "SystemControlBackgroundBaseLowBrush" },
            { "CalendarDatePickerBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "CalendarDatePickerBackgroundFocused", "SystemControlHighlightListAccentLowBrush" },
            { "CalendarDatePickerBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "CalendarDatePickerBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "CalendarDatePickerBorderBrushPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "CalendarDatePickerBorderBrushDisabled", "SystemControlDisabledBaseLowBrush" },
            { "CalendarViewFocusBorderBrush", "SystemControlForegroundBaseHighBrush" },
            { "CalendarViewSelectedHoverBorderBrush", "SystemControlHighlightListAccentMediumBrush" },
            { "CalendarViewSelectedPressedBorderBrush", "SystemControlHighlightListAccentHighBrush" },
            { "CalendarViewSelectedBorderBrush", "SystemControlHighlightAccentBrush" },
            { "CalendarViewHoverBorderBrush", "SystemControlHighlightBaseMediumLowBrush" },
            { "CalendarViewPressedBorderBrush", "SystemControlHighlightBaseMediumBrush" },
            { "CalendarViewTodayForeground", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CalendarViewBlackoutForeground", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarViewSelectedForeground", "SystemControlHighlightBaseHighBrush" },
            { "CalendarViewPressedForeground", "SystemControlHighlightBaseHighBrush" },
            { "CalendarViewOutOfScopeForeground", "SystemControlHyperlinkBaseHighBrush" },
            { "CalendarViewCalendarItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "CalendarViewOutOfScopeBackground", "SystemControlDisabledChromeMediumLowBrush" },
            { "CalendarViewCalendarItemBackground", "SystemControlBackgroundAltHighBrush" },
            { "CalendarViewForeground", "SystemControlHyperlinkBaseMediumHighBrush" },
            { "CalendarViewBackground", "SystemControlBackgroundAltHighBrush" },
            { "CalendarViewBorderBrush", "SystemControlForegroundChromeMediumBrush" },
            { "CalendarViewWeekDayForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarViewNavigationButtonBackground", "SystemControlTransparentBrush" },
            { "CalendarViewNavigationButtonForegroundPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "CalendarViewNavigationButtonForegroundPressed", "SystemControlForegroundBaseMediumBrush" },
            { "CalendarViewNavigationButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarViewNavigationButtonBorderBrushPointerOver", "SystemControlHighlightTransparentBrush" },
            { "CalendarViewNavigationButtonBorderBrush", "SystemControlTransparentBrush" },
            { "HubForeground", "SystemControlPageTextBaseHighBrush" },
            { "HubSectionHeaderButtonForeground", "SystemControlHyperlinkTextBrush" },
            { "HubSectionHeaderButtonForegroundPointerOver", "SystemControlHyperlinkBaseMediumBrush" },
            { "HubSectionHeaderButtonForegroundPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "HubSectionHeaderButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "HubSectionHeaderForeground", "SystemControlPageTextBaseHighBrush" },
            { "FlipViewBackground", "SystemControlPageBackgroundListLowBrush" },
            { "FlipViewNextPreviousButtonBackground", "SystemControlBackgroundBaseMediumLowBrush" },
            { "FlipViewNextPreviousButtonBackgroundPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "FlipViewNextPreviousButtonBackgroundPressed", "SystemControlHighlightBaseMediumHighBrush" },
            { "FlipViewNextPreviousArrowForeground", "SystemControlForegroundAltMediumHighBrush" },
            { "FlipViewNextPreviousArrowForegroundPointerOver", "SystemControlHighlightAltAltMediumHighBrush" },
            { "FlipViewNextPreviousArrowForegroundPressed", "SystemControlHighlightAltAltMediumHighBrush" },
            { "FlipViewNextPreviousButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "FlipViewNextPreviousButtonBorderBrushPointerOver", "SystemControlForegroundTransparentBrush" },
            { "FlipViewNextPreviousButtonBorderBrushPressed", "SystemControlForegroundTransparentBrush" },
            { "FlipViewItemBackground", "SystemControlTransparentBrush" },
            { "DateTimePickerFlyoutButtonBackground", "SystemControlTransparentBrush" },
            { "DateTimePickerFlyoutButtonBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "DateTimePickerFlyoutButtonBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "DateTimePickerFlyoutButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "DateTimePickerFlyoutButtonBorderBrushPointerOver", "SystemControlHighlightTransparentBrush" },
            { "DateTimePickerFlyoutButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "DateTimePickerFlyoutButtonForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "DateTimePickerFlyoutButtonForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "DatePickerSpacerFill", "SystemControlForegroundBaseLowBrush" },
            { "DatePickerSpacerFillDisabled", "SystemControlDisabledBaseLowBrush" },
            { "DatePickerHeaderForeground", "SystemControlForegroundBaseHighBrush" },
            { "DatePickerHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "DatePickerButtonBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "DatePickerButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "DatePickerButtonBorderBrushPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "DatePickerButtonBorderBrushDisabled", "SystemControlDisabledBaseLowBrush" },
            { "DatePickerButtonBackground", "SystemControlBackgroundAltMediumLowBrush" },
            { "DatePickerButtonBackgroundPointerOver", "SystemControlPageBackgroundAltMediumBrush" },
            { "DatePickerButtonBackgroundPressed", "SystemControlBackgroundBaseLowBrush" },
            { "DatePickerButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "DatePickerButtonBackgroundFocused", "SystemControlHighlightListAccentLowBrush" },
            { "DatePickerButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "DatePickerButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "DatePickerButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "DatePickerButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "DatePickerButtonForegroundFocused", "SystemControlHighlightAltBaseHighBrush" },
            { "DatePickerFlyoutPresenterBackground", "SystemControlTransientBackgroundBrush" },
            { "DatePickerFlyoutPresenterBorderBrush", "SystemControlTransientBorderBrush" },
            { "DatePickerFlyoutPresenterSpacerFill", "SystemControlForegroundBaseLowBrush" },
            { "DatePickerFlyoutPresenterHighlightFill", "SystemControlHighlightListAccentLowBrush" },
            { "TimePickerSpacerFill", "SystemControlForegroundBaseLowBrush" },
            { "TimePickerSpacerFillDisabled", "SystemControlDisabledBaseLowBrush" },
            { "TimePickerHeaderForeground", "SystemControlForegroundBaseHighBrush" },
            { "TimePickerHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TimePickerButtonBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "TimePickerButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "TimePickerButtonBorderBrushPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "TimePickerButtonBorderBrushDisabled", "SystemControlDisabledBaseLowBrush" },
            { "TimePickerButtonBackground", "SystemControlBackgroundAltMediumLowBrush" },
            { "TimePickerButtonBackgroundPointerOver", "SystemControlPageBackgroundAltMediumBrush" },
            { "TimePickerButtonBackgroundPressed", "SystemControlBackgroundBaseLowBrush" },
            { "TimePickerButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "TimePickerButtonBackgroundFocused", "SystemControlHighlightListAccentLowBrush" },
            { "TimePickerButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "TimePickerButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "TimePickerButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "TimePickerButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TimePickerButtonForegroundFocused", "SystemControlHighlightAltBaseHighBrush" },
            { "TimePickerFlyoutPresenterBackground", "SystemControlTransientBackgroundBrush" },
            { "TimePickerFlyoutPresenterBorderBrush", "SystemControlTransientBorderBrush" },
            { "TimePickerFlyoutPresenterSpacerFill", "SystemControlForegroundBaseLowBrush" },
            { "TimePickerFlyoutPresenterHighlightFill", "SystemControlHighlightListAccentLowBrush" },
            { "LoopingSelectorButtonBackground", "SystemControlBackgroundChromeMediumBrush" },
            { "LoopingSelectorItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "LoopingSelectorItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "LoopingSelectorItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "LoopingSelectorItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "LoopingSelectorItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "LoopingSelectorItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "TextControlForeground", "SystemControlForegroundBaseHighBrush" },
            { "TextControlForegroundPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "TextControlForegroundFocused", "SystemControlForegroundChromeBlackHighBrush" },
            { "TextControlForegroundDisabled", "SystemControlDisabledChromeDisabledLowBrush" },
            { "TextControlBackground", "SystemControlBackgroundAltMediumLowBrush" },
            { "TextControlBackgroundPointerOver", "SystemControlBackgroundAltMediumBrush" },
            { "TextControlBackgroundFocused", "SystemControlBackgroundChromeWhiteBrush" },
            { "TextControlBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "TextControlBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "TextControlBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "TextControlBorderBrushFocused", "SystemControlHighlightAccentBrush" },
            { "TextControlBorderBrushDisabled", "SystemControlDisabledBaseLowBrush" },
            { "TextControlPlaceholderForeground", "SystemControlPageTextBaseMediumBrush" },
            { "TextControlPlaceholderForegroundPointerOver", "SystemControlPageTextBaseMediumBrush" },
            { "TextControlPlaceholderForegroundFocused", "SystemControlPageTextChromeBlackMediumLowBrush" },
            { "TextControlPlaceholderForegroundDisabled", "SystemControlDisabledChromeDisabledLowBrush" },
            { "TextControlHeaderForeground", "SystemControlForegroundBaseHighBrush" },
            { "TextControlHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TextControlSelectionHighlightColor", "SystemControlHighlightAccentBrush" },
            { "TextControlButtonBackground", "SystemControlTransparentBrush" },
            { "TextControlButtonBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "TextControlButtonBackgroundPressed", "SystemControlHighlightAccentBrush" },
            { "TextControlButtonBorderBrush", "SystemControlTransparentBrush" },
            { "TextControlButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "TextControlButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "TextControlButtonForeground", "SystemControlForegroundChromeBlackMediumBrush" },
            { "TextControlButtonForegroundPointerOver", "SystemControlHighlightAccentBrush" },
            { "TextControlButtonForegroundPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ContentLinkForegroundColor", "SystemControlHyperlinkTextBrush" },
            { "ContentLinkBackgroundColor", "SystemControlHighlightChromeAltLowBrush" },
            { "TextControlHighlighterForeground", "SystemControlForegroundAltHighBrush" },
            { "TextControlHighlighterBackground", Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00) },
            { "FlyoutPresenterBackground", "SystemControlTransientBackgroundBrush" },
            { "FlyoutBorderThemeBrush", "SystemControlTransientBorderBrush" },
            { "ToggleMenuFlyoutItemBackground", "SystemControlTransparentBrush" },
            { "ToggleMenuFlyoutItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "ToggleMenuFlyoutItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "ToggleMenuFlyoutItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "ToggleMenuFlyoutItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "ToggleMenuFlyoutItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "ToggleMenuFlyoutItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ToggleMenuFlyoutItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleMenuFlyoutItemKeyboardAcceleratorTextForeground", "SystemControlForegroundBaseMediumBrush" },
            { "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "SystemControlHighlightAltBaseMediumBrush" },
            { "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleMenuFlyoutItemCheckGlyphForeground", "SystemControlForegroundBaseMediumHighBrush" },
            { "ToggleMenuFlyoutItemCheckGlyphForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "ToggleMenuFlyoutItemCheckGlyphForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ToggleMenuFlyoutItemCheckGlyphForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "PivotBackground", "SystemControlTransparentBrush" },
            { "PivotHeaderBackground", "SystemControlTransparentBrush" },
            { "PivotNextButtonBackground", "SystemControlBackgroundBaseMediumLowBrush" },
            { "PivotNextButtonBackgroundPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "PivotNextButtonBackgroundPressed", "SystemControlHighlightBaseMediumHighBrush" },
            { "PivotNextButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "PivotNextButtonBorderBrushPointerOver", "SystemControlForegroundTransparentBrush" },
            { "PivotNextButtonBorderBrushPressed", "SystemControlForegroundTransparentBrush" },
            { "PivotNextButtonForeground", "SystemControlForegroundAltMediumHighBrush" },
            { "PivotNextButtonForegroundPointerOver", "SystemControlHighlightAltAltMediumHighBrush" },
            { "PivotNextButtonForegroundPressed", "SystemControlHighlightAltAltMediumHighBrush" },
            { "PivotPreviousButtonBackground", "SystemControlBackgroundBaseMediumLowBrush" },
            { "PivotPreviousButtonBackgroundPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "PivotPreviousButtonBackgroundPressed", "SystemControlHighlightBaseMediumHighBrush" },
            { "PivotPreviousButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "PivotPreviousButtonBorderBrushPointerOver", "SystemControlForegroundTransparentBrush" },
            { "PivotPreviousButtonBorderBrushPressed", "SystemControlForegroundTransparentBrush" },
            { "PivotPreviousButtonForeground", "SystemControlForegroundAltMediumHighBrush" },
            { "PivotPreviousButtonForegroundPointerOver", "SystemControlHighlightAltAltMediumHighBrush" },
            { "PivotPreviousButtonForegroundPressed", "SystemControlHighlightAltAltMediumHighBrush" },
            { "PivotItemBackground", "SystemControlTransparentBrush" },
            { "PivotHeaderItemBackgroundUnselected", "SystemControlTransparentBrush" },
            { "PivotHeaderItemBackgroundUnselectedPointerOver", "SystemControlHighlightTransparentBrush" },
            { "PivotHeaderItemBackgroundUnselectedPressed", "SystemControlHighlightTransparentBrush" },
            { "PivotHeaderItemBackgroundSelected", "SystemControlHighlightTransparentBrush" },
            { "PivotHeaderItemBackgroundSelectedPointerOver", "SystemControlHighlightTransparentBrush" },
            { "PivotHeaderItemBackgroundSelectedPressed", "SystemControlHighlightTransparentBrush" },
            { "PivotHeaderItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "PivotHeaderItemForegroundUnselected", "SystemControlForegroundBaseMediumBrush" },
            { "PivotHeaderItemForegroundUnselectedPointerOver", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "PivotHeaderItemForegroundUnselectedPressed", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "PivotHeaderItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "PivotHeaderItemForegroundSelectedPointerOver", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "PivotHeaderItemForegroundSelectedPressed", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "PivotHeaderItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "PivotHeaderItemFocusPipeFill", "SystemControlHighlightAltAccentBrush" },
            { "PivotHeaderItemSelectedPipeFill", "SystemControlHighlightAltAccentBrush" },
            { "GridViewHeaderItemBackground", "SystemControlTransparentBrush" },
            { "GridViewHeaderItemDividerStroke", "SystemControlForegroundBaseLowBrush" },
            { "GridViewItemBackground", "SystemControlTransparentBrush" },
            { "GridViewItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "GridViewItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "GridViewItemBackgroundSelected", "SystemControlHighlightAccentBrush" },
            { "GridViewItemBackgroundSelectedPointerOver", "SystemControlHighlightListAccentMediumBrush" },
            { "GridViewItemBackgroundSelectedPressed", "SystemControlHighlightListAccentHighBrush" },
            { "GridViewItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "GridViewItemForegroundPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "GridViewItemForegroundSelected", "SystemControlForegroundBaseHighBrush" },
            { "GridViewItemFocusVisualPrimaryBrush", "SystemControlFocusVisualPrimaryBrush" },
            { "GridViewItemFocusVisualSecondaryBrush", "SystemControlFocusVisualSecondaryBrush" },
            { "GridViewItemFocusBorderBrush", "SystemControlForegroundAltHighBrush" },
            { "GridViewItemFocusSecondaryBorderBrush", "SystemControlForegroundBaseHighBrush" },
            { "GridViewItemCheckBrush", "SystemControlForegroundBaseMediumHighBrush" },
            { "GridViewItemCheckBoxBrush", "SystemControlBackgroundChromeMediumBrush" },
            { "GridViewItemDragBackground", "SystemControlTransparentBrush" },
            { "GridViewItemDragForeground", "SystemControlHighlightAltChromeWhiteBrush" },
            { "GridViewItemPlaceholderBackground", "SystemControlDisabledChromeDisabledHighBrush" },
            { "MediaTransportControlsPanelBackground", "SystemControlPageBackgroundAltMediumBrush" },
            { "MediaTransportControlsFlyoutBackground", "SystemControlTransientBackgroundBrush" },
            { "AppBarLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "CalendarDatePickerLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "ComboBoxLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "DatePickerLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "FlyoutLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "PopupLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "SplitViewLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "TimePickerLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "JumpListDefaultEnabledBackground", "SystemControlBackgroundAccentBrush" },
            { "JumpListDefaultEnabledForeground", "SystemControlForegroundChromeWhiteBrush" },
            { "JumpListDefaultDisabledBackground", "SystemControlBackgroundBaseLowBrush" },
            { "JumpListDefaultDisabledForeground", "SystemControlDisabledBaseMediumLowBrush" },
            { "KeyTipForeground", "SystemControlBackgroundChromeWhiteBrush" },
            { "KeyTipBackground", "SystemControlForegroundChromeGrayBrush" },
            { "KeyTipBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "SystemChromeAltMediumHighColor", Color.FromArgb(0xCC, 0x1F, 0x1F, 0x1F) },
            { "SystemChromeAltHighColor", Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1C) },
            { "SystemRevealAltHighColor", Color.FromArgb(0xF2, 0x00, 0x00, 0x00) },
            { "SystemRevealAltLowColor", Color.FromArgb(0x30, 0x00, 0x00, 0x00) },
            { "SystemRevealAltMediumColor", Color.FromArgb(0x91, 0x00, 0x00, 0x00) },
            { "SystemRevealAltMediumHighColor", Color.FromArgb(0xC2, 0x00, 0x00, 0x00) },
            { "SystemRevealAltMediumLowColor", Color.FromArgb(0x61, 0x00, 0x00, 0x00) },
            { "SystemRevealBaseHighColor", Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealBaseLowColor", Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealBaseMediumColor", Color.FromArgb(0x91, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealBaseMediumHighColor", Color.FromArgb(0xC2, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealBaseMediumLowColor", Color.FromArgb(0x61, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealChromeAltLowColor", Color.FromArgb(0xF2, 0xF9, 0xF9, 0xF9) },
            { "SystemRevealChromeBlackHighColor", Color.FromArgb(0xF2, 0x00, 0x00, 0x00) },
            { "SystemRevealChromeBlackLowColor", Color.FromArgb(0x30, 0x00, 0x00, 0x00) },
            { "SystemRevealChromeBlackMediumLowColor", Color.FromArgb(0x61, 0x00, 0x00, 0x00) },
            { "SystemRevealChromeBlackMediumColor", Color.FromArgb(0xC2, 0x00, 0x00, 0x00) },
            { "SystemRevealChromeHighColor", Color.FromArgb(0xF2, 0x76, 0x76, 0x76) },
            { "SystemRevealChromeLowColor", Color.FromArgb(0xF2, 0x1F, 0x1F, 0x1F) },
            { "SystemRevealChromeMediumColor", Color.FromArgb(0xF2, 0x39, 0x39, 0x39) },
            { "SystemRevealChromeMediumLowColor", Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B) },
            { "SystemRevealChromeWhiteColor", Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealChromeGrayColor", Color.FromArgb(0xF2, 0x76, 0x76, 0x76) },
            { "SystemRevealListLowColor", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealListMediumColor", Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) },
            { "MenuFlyoutPresenterBackground", "SystemControlTransientBackgroundBrush" },
            { "MenuFlyoutPresenterBorderBrush", "SystemControlTransientBorderBrush" },
            { "MenuFlyoutItemBackground", "SystemControlTransparentBrush" },
            { "MenuFlyoutItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "MenuFlyoutItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "MenuFlyoutItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "MenuFlyoutItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "MenuFlyoutItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "MenuFlyoutSubItemBackground", "SystemControlTransparentBrush" },
            { "MenuFlyoutSubItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "MenuFlyoutSubItemBackgroundPressed", "SystemControlHighlightListAccentHighBrush" },
            { "MenuFlyoutSubItemBackgroundSubMenuOpened", "SystemControlHighlightListAccentLowBrush" },
            { "MenuFlyoutSubItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "MenuFlyoutSubItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "MenuFlyoutSubItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemForegroundSubMenuOpened", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "MenuFlyoutSubItemChevron", "SystemControlForegroundBaseMediumHighBrush" },
            { "MenuFlyoutSubItemChevronPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemChevronPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemChevronSubMenuOpened", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemChevronDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "MenuFlyoutLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "MenuFlyoutItemFocusedBackgroundThemeBrush", Color.FromArgb(0xFF, 0x21, 0x21, 0x21) },
            { "MenuFlyoutItemFocusedForegroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "MenuFlyoutItemDisabledForegroundThemeBrush", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) },
            { "MenuFlyoutItemPointerOverBackgroundThemeBrush", Color.FromArgb(0xFF, 0x21, 0x21, 0x21) },
            { "MenuFlyoutItemPointerOverForegroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "MenuFlyoutItemPressedBackgroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "MenuFlyoutItemPressedForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "MenuFlyoutSeparatorThemeBrush", Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A) },
            { "RatingControlUnselectedForeground", "SystemControlBackgroundBaseLowBrush" },
            { "RatingControlSelectedForeground", "SystemControlForegroundAccentBrush" },
            { "RatingControlPlaceholderForeground", "SystemControlForegroundBaseHighBrush" },
            { "RatingControlPointerOverPlaceholderForeground", "SystemControlForegroundBaseMediumBrush" },
            { "RatingControlPointerOverUnselectedForeground", "SystemControlForegroundBaseMediumBrush" },
            { "RatingControlPointerOverSelectedForeground", "SystemControlForegroundAccentBrush" },
            { "RatingControlDisabledSelectedForeground", "SystemBaseMediumLowColor" },
            { "RatingControlCaptionForeground", "SystemControlForegroundBaseMediumBrush" },
            { "NavigationViewExpandedPaneBackground", "SystemChromeMediumColor" },
            { "SystemChromeMediumHighColor", Color.FromArgb(0xFF, 0x32, 0x32, 0x32) },
            { "NavigationViewItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "NavigationViewItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "NavigationViewItemForegroundChecked", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundCheckedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundCheckedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "NavigationViewItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundSelectedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundSelectedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundSelectedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "NavigationViewItemBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "NavigationViewItemBorderBrushCheckedDisabled", "SystemControlTransparentBrush" },
            { "NavigationViewItemBorderBrushSelectedDisabled", "SystemControlTransparentBrush" },
            { "NavigationViewSelectionIndicatorForeground", "SystemControlForegroundAccentBrush" },
            { "TopNavigationViewItemForeground", "SystemControlForegroundBaseMediumBrush" },
            { "TopNavigationViewItemForegroundPointerOver", "SystemControlForegroundBaseMediumHighBrush" },
            { "TopNavigationViewItemForegroundPressed", "SystemControlForegroundBaseMediumHighBrush" },
            { "TopNavigationViewItemForegroundSelected", "SystemControlForegroundBaseHighBrush" },
            { "TopNavigationViewItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TopNavigationViewItemBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "TopNavigationViewItemBackgroundPressed", "SystemControlTransparentBrush" },
            { "TopNavigationViewItemBackgroundSelected", "SystemControlTransparentBrush" },
            { "NavigationViewBackButtonBackground", "SystemControlTransparentBrush" },
            { "ColorPickerSliderThumbBackground", "SystemControlForegroundBaseHighBrush" },
            { "ColorPickerSliderThumbBackgroundPointerOver", "SystemControlHighlightChromeAltLowBrush" },
            { "ColorPickerSliderThumbBackgroundPressed", "SystemControlForegroundChromeHighBrush" },
            { "ColorPickerSliderThumbBackgroundDisabled", "SystemControlDisabledChromeDisabledHighBrush" },
            { "ColorPickerSliderTrackFillDisabled", "SystemControlDisabledBaseLowBrush" },
            { "PersonPictureForegroundThemeBrush", "SystemAltHighColor" },
            { "PersonPictureEllipseBadgeForegroundThemeBrush", "SystemBaseHighColor" },
            { "PersonPictureEllipseBadgeFillThemeBrush", "SystemChromeDisabledHighColor" },
            { "PersonPictureEllipseBadgeStrokeThemeBrush", "SystemListMediumColor" },
            { "PersonPictureEllipseFillThemeBrush", "SystemBaseMediumColor" },
            { "RefreshContainerForegroundBrush", "White" },
            { "RefreshContainerBackgroundBrush", "Transparent" },
            { "RefreshVisualizerForeground", "White" },
            { "RefreshVisualizerBackground", "Transparent" },
            { "MenuBarBackground", "SystemControlTransparentBrush" },
            { "MenuBarItemBackground", "SystemControlTransparentBrush" },
            { "MenuBarItemBackgroundPointerOver", "SystemControlBackgroundListLowBrush" },
            { "MenuBarItemBackgroundPressed", "SystemControlBackgroundListMediumBrush" },
            { "MenuBarItemBackgroundSelected", "SystemControlBackgroundListMediumBrush" },
            { "MenuBarItemBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "MenuBarItemBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "MenuBarItemBorderBrushPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "MenuBarItemBorderBrushSelected", "SystemControlHighlightBaseMediumLowBrush" },
            { "AppBarButtonBackground", "SystemControlTransparentBrush" },
            { "AppBarButtonBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "AppBarButtonBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "AppBarButtonBackgroundDisabled", "SystemControlTransparentBrush" },
            { "AppBarButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "AppBarButtonForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarButtonBorderBrush", "SystemControlTransparentBrush" },
            { "AppBarButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "AppBarButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "AppBarButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBackground", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBackgroundDisabled", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBackgroundChecked", "SystemControlHighlightAccentBrush" },
            { "AppBarToggleButtonBackgroundCheckedPointerOver", "SystemControlHighlightAccentBrush" },
            { "AppBarToggleButtonBackgroundCheckedPressed", "SystemControlHighlightAccentBrush" },
            { "AppBarToggleButtonBackgroundCheckedDisabled", "SystemControlDisabledAccentBrush" },
            { "AppBarToggleButtonBackgroundHighLightOverlay", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBackgroundHighLightOverlayPointerOver", "SystemControlHighlightListLowBrush" },
            { "AppBarToggleButtonBackgroundHighLightOverlayPressed", "SystemControlHighlightListMediumBrush" },
            { "AppBarToggleButtonBackgroundHighLightOverlayCheckedPointerOver", "SystemControlHighlightListLowBrush" },
            { "AppBarToggleButtonBackgroundHighLightOverlayCheckedPressed", "SystemControlHighlightListMediumBrush" },
            { "AppBarToggleButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "AppBarToggleButtonForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonForegroundChecked", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonForegroundCheckedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonForegroundCheckedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonForegroundCheckedDisabled", "SystemControlBackgroundBaseMediumLowBrush" },
            { "AppBarToggleButtonBorderBrush", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushChecked", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushCheckedPointerOver", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushCheckedPressed", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushCheckedDisabled", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonCheckGlyphForeground", "SystemControlForegroundBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundChecked", "SystemControlForegroundBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundCheckedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundCheckedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundCheckedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundCheckedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonCheckedBackgroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "AppBarToggleButtonCheckedBorderThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "AppBarToggleButtonCheckedDisabledBackgroundThemeBrush", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) },
            { "AppBarToggleButtonCheckedDisabledBorderThemeBrush", "Transparent" },
            { "AppBarToggleButtonCheckedDisabledForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "AppBarToggleButtonCheckedForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "AppBarToggleButtonCheckedPointerOverBackgroundThemeBrush", Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) },
            { "AppBarToggleButtonCheckedPointerOverBorderThemeBrush", Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) },
            { "AppBarToggleButtonCheckedPressedBackgroundThemeBrush", "Transparent" },
            { "AppBarToggleButtonCheckedPressedBorderThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "AppBarToggleButtonCheckedPressedForegroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "AppBarToggleButtonPointerOverBackgroundThemeBrush", Color.FromArgb(0x21, 0xFF, 0xFF, 0xFF) },
            { "ListBoxBackgroundThemeBrush", Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) },
            { "ListBoxBorderThemeBrush", "Transparent" },
            { "ListBoxDisabledForegroundThemeBrush", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) },
            { "ListBoxFocusBackgroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ListBoxForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "ListBoxItemDisabledForegroundThemeBrush", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) },
            { "ListBoxItemPointerOverBackgroundThemeBrush", Color.FromArgb(0x21, 0x00, 0x00, 0x00) },
            { "ListBoxItemPointerOverForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "ListBoxItemPressedBackgroundThemeBrush", Color.FromArgb(0xFF, 0xD3, 0xD3, 0xD3) },
            { "ListBoxItemPressedForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "ListBoxItemSelectedBackgroundThemeBrush", Color.FromArgb(0xFF, 0x46, 0x17, 0xB4) },
            { "ListBoxItemSelectedDisabledBackgroundThemeBrush", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) },
            { "ListBoxItemSelectedDisabledForegroundThemeBrush", Color.FromArgb(0x99, 0x00, 0x00, 0x00) },
            { "ListBoxItemSelectedForegroundThemeBrush", "White" },
            { "ListBoxItemSelectedPointerOverBackgroundThemeBrush", Color.FromArgb(0xFF, 0x5F, 0x37, 0xBE) },
            { "CommandBarBackground", "SystemControlBackgroundChromeMediumBrush" },
            { "CommandBarForeground", "SystemControlForegroundBaseHighBrush" },
            { "CommandBarHighContrastBorder", "SystemControlForegroundTransparentBrush" },
            { "CommandBarEllipsisIconForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CommandBarOverflowPresenterBackground", "SystemControlTransientBackgroundBrush" },
            { "CommandBarOverflowPresenterBorderBrush", "SystemControlTransientBorderBrush" },
            { "CommandBarLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "ListViewItemBackground", "SystemControlTransparentBrush" },
            { "ListViewItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "ListViewItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "ListViewItemBackgroundSelected", "SystemControlHighlightListAccentLowBrush" },
            { "ListViewItemBackgroundSelectedPointerOver", "SystemControlHighlightListAccentMediumBrush" },
            { "ListViewItemBackgroundSelectedPressed", "SystemControlHighlightListAccentHighBrush" },
            { "ListViewItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "ListViewItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "ListViewItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "ListViewItemFocusVisualPrimaryBrush", "SystemControlFocusVisualPrimaryBrush" },
            { "ListViewItemFocusVisualSecondaryBrush", "SystemControlFocusVisualSecondaryBrush" },
            { "ListViewItemFocusBorderBrush", "SystemControlForegroundAltHighBrush" },
            { "ListViewItemFocusSecondaryBorderBrush", "SystemControlForegroundBaseHighBrush" },
            { "ListViewItemCheckBrush", "SystemControlForegroundBaseMediumHighBrush" },
            { "ListViewItemCheckBoxBrush", "SystemControlForegroundBaseMediumHighBrush" },
            { "ListViewItemDragBackground", "SystemControlTransparentBrush" },
            { "ListViewItemDragForeground", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ListViewItemPlaceholderBackground", "SystemControlDisabledChromeDisabledHighBrush" },
            { "ListViewItemCheckHintThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ListViewItemCheckSelectingThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ListViewItemCheckThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ListViewItemDragBackgroundThemeBrush", Color.FromArgb(0x99, 0x46, 0x17, 0xB4) },
            { "ListViewItemDragForegroundThemeBrush", "White" },
            { "ListViewItemFocusBorderThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ListViewItemOverlayBackgroundThemeBrush", Color.FromArgb(0xA6, 0x00, 0x00, 0x00) },
            { "ListViewItemOverlayForegroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ListViewItemOverlaySecondaryForegroundThemeBrush", Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) },
            { "ListViewItemPlaceholderBackgroundThemeBrush", Color.FromArgb(0xFF, 0x3D, 0x3D, 0x3D) },
            { "ListViewItemPointerOverBackgroundThemeBrush", Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF) },
            { "ListViewItemSelectedBackgroundThemeBrush", Color.FromArgb(0xFF, 0x46, 0x17, 0xB4) },
            { "ListViewItemSelectedForegroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ListViewItemSelectedPointerOverBackgroundThemeBrush", Color.FromArgb(0xFF, 0x5F, 0x37, 0xBE) },
            { "ListViewItemSelectedPointerOverBorderThemeBrush", Color.FromArgb(0xFF, 0x5F, 0x37, 0xBE) },
            { "TextBoxForegroundHeaderThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "TextBoxPlaceholderTextThemeBrush", Color.FromArgb(0xAB, 0x00, 0x00, 0x00) },
            { "TextBoxBackgroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "TextBoxBorderThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "TextBoxButtonBackgroundThemeBrush", "Transparent" },
            { "TextBoxButtonBorderThemeBrush", "Transparent" },
            { "TextBoxButtonForegroundThemeBrush", Color.FromArgb(0x99, 0x00, 0x00, 0x00) },
            { "TextBoxButtonPointerOverBackgroundThemeBrush", Color.FromArgb(0xFF, 0xDE, 0xDE, 0xDE) },
            { "TextBoxButtonPointerOverBorderThemeBrush", "Transparent" },
            { "TextBoxButtonPointerOverForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "TextBoxButtonPressedBackgroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "TextBoxButtonPressedBorderThemeBrush", "Transparent" },
            { "TextBoxButtonPressedForegroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "TextBoxDisabledBackgroundThemeBrush", "Transparent" },
            { "TextBoxDisabledBorderThemeBrush", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) },
            { "TextBoxDisabledForegroundThemeBrush", Color.FromArgb(0xFF, 0x66, 0x66, 0x66) },
            { "TextBoxForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "AutoSuggestBoxSuggestionsListBackground", "SystemControlTransientBackgroundBrush" },
            { "AutoSuggestBoxSuggestionsListBorderBrush", "SystemControlTransientBorderBrush" },
            { "AutoSuggestBoxLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "TreeViewItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "TreeViewItemBackgroundSelectedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TreeViewItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "TreeViewItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "TreeViewItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "TreeViewItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TreeViewItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "TreeViewItemForegroundSelectedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "TreeViewItemForegroundSelectedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "TreeViewItemForegroundSelectedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TreeViewItemBorderBrush", "SystemControlTransparentBrush" },
            { "TreeViewItemBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "TreeViewItemBorderBrushSelected", "SystemControlTransparentBrush" },
            { "TreeViewItemBorderBrushSelectedDisabled", "SystemControlTransparentBrush" },
            { "TreeViewItemCheckBoxBackgroundSelected", "SystemControlTransparentBrush" },
            { "TreeViewItemCheckBoxBorderSelected", "SystemControlForegroundBaseMediumHighBrush" },
            { "TreeViewItemCheckGlyphSelected", "SystemControlForegroundBaseMediumHighBrush" },
            { "SwipeItemBackground", "SystemControlBackgroundBaseLowBrush" },
            { "SwipeItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "SwipeItemBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "SwipeItemPreThresholdExecuteForeground", "SystemControlBackgroundBaseMediumBrush" },
            { "SwipeItemPreThresholdExecuteBackground", "SystemControlBackgroundBaseLowBrush" },
            { "SwipeItemPostThresholdExecuteForeground", "SystemControlForegroundChromeWhiteBrush" },
            { "SwipeItemPostThresholdExecuteBackground", "SystemControlBackgroundAccentBrush" },
            { "SplitButtonBackground", "SystemControlBackgroundBaseLowBrush" },
            { "SplitButtonBackgroundPointerOver", "SystemControlBackgroundBaseLowBrush" },
            { "SplitButtonBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "SplitButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "SplitButtonBackgroundChecked", "SystemControlHighlightAccentBrush" },
            { "SplitButtonBackgroundCheckedPointerOver", "SystemControlHighlightAccentBrush" },
            { "SplitButtonBackgroundCheckedPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "SplitButtonBackgroundCheckedDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "SplitButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "SplitButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "SplitButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "SplitButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "SplitButtonForegroundChecked", "SystemControlHighlightAltChromeWhiteBrush" },
            { "SplitButtonForegroundCheckedPointerOver", "SystemControlHighlightAltChromeWhiteBrush" },
            { "SplitButtonForegroundCheckedPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "SplitButtonForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "SplitButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "SplitButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "SplitButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "SplitButtonBorderBrushDisabled", "SystemControlDisabledTransparentBrush" },
            { "SplitButtonBorderBrushChecked", "SystemControlHighlightAltTransparentBrush" },
            { "SplitButtonBorderBrushCheckedPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "SplitButtonBorderBrushCheckedPressed", "SystemControlHighlightAltTransparentBrush" },
            { "SplitButtonBorderBrushCheckedDisabled", "SystemControlDisabledTransparentBrush" },
            { "CommandBarFlyoutButtonBackground", "SystemControlTransparentBrush" },
            { "MenuFlyoutItemKeyboardAcceleratorTextForeground", "SystemControlForegroundBaseMediumBrush" },
            { "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "SystemControlHighlightAltBaseMediumBrush" },
            { "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarButtonKeyboardAcceleratorTextForeground", "SystemControlForegroundBaseMediumBrush" },
            { "AppBarButtonKeyboardAcceleratorTextForegroundPointerOver", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarButtonKeyboardAcceleratorTextForegroundPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarButtonKeyboardAcceleratorTextForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForeground", "SystemControlForegroundBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundPointerOver", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundChecked", "SystemControlForegroundBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedPointerOver", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarButtonBackgroundSubMenuOpened", "SystemControlHighlightListAccentLowBrush" },
            { "AppBarButtonForegroundSubMenuOpened", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonKeyboardAcceleratorTextForegroundSubMenuOpened", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarButtonBorderBrushSubMenuOpened", "SystemControlTransparentBrush" },
            { "AppBarButtonSubItemChevronForeground", "SystemControlForegroundBaseMediumHighBrush" },
            { "AppBarButtonSubItemChevronForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonSubItemChevronForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonSubItemChevronForegroundSubMenuOpened", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonSubItemChevronForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
        };

        private readonly Dictionary<string, object> _defaultLight = new Dictionary<string, object>
        {
            { "ApplicationPageBackgroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "PageHeaderForegroundBrush", "SystemBaseHighColor" },
            { "PageHeaderHighlightBrush", "SystemAccentColor" },
            { "PageHeaderDisabledBrush", "SystemControlForegroundBaseMediumBrush" },
            { "PageHeaderBackgroundBrush", "SystemControlBackgroundChromeMediumBrush" },
            { "PageSubHeaderBackgroundBrush", "SystemControlBackgroundChromeMediumLowBrush" },
            { "TelegramSeparatorMediumBrush", "SystemControlBackgroundChromeMediumLowBrush" },
            { "ChatOnlineBadgeBrush", Color.FromArgb(0xFF, 0x00, 0xB1, 0x2C) },
            { "ChatVerifiedBadgeBrush", "SystemAccentColor" },
            { "ChatLastMessageStateBrush", "SystemAccentColor" },
            { "ChatFromLabelBrush", Color.FromArgb(0xFF, 0x3C, 0x7E, 0xB0) },
            { "ChatDraftLabelBrush", Color.FromArgb(0xFF, 0xDD, 0x4B, 0x39) },
            { "ChatUnreadBadgeMutedBrush", Color.FromArgb(0xFF, 0xBB, 0xBB, 0xBB) },
            { "ChatUnreadLabelMutedBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ChatFailedBadgeBrush", Color.FromArgb(0xFF, 0xFF, 0x00, 0x00) },
            { "ChatFailedLabelBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ChatUnreadBadgeBrush", "SystemAccentColor" },
            { "ChatUnreadLabelBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "MessageForegroundColor", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "MessageForegroundOutColor", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "MessageForegroundLinkColor", Color.FromArgb(0xFF, 0x16, 0x8A, 0xCD) },
            { "MessageForegroundLinkOutColor", Color.FromArgb(0xFF, 0x16, 0x8A, 0xCD) },
            { "MessageBackgroundColor", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "MessageBackgroundOutColor", Color.FromArgb(0xFF, 0xF0, 0xFD, 0xDF) },
            { "MessageSubtleLabelColor", Color.FromArgb(0xFF, 0xA1, 0xAD, 0xB6) },
            { "MessageSubtleLabelOutColor", Color.FromArgb(0xFF, 0x6D, 0xC2, 0x64) },
            { "MessageSubtleGlyphColor", Color.FromArgb(0xFF, 0xA1, 0xAD, 0xB6) },
            { "MessageSubtleGlyphOutColor", Color.FromArgb(0xFF, 0x5D, 0xC4, 0x52) },
            { "MessageSubtleForegroundColor", Color.FromArgb(0xFF, 0xA1, 0xAD, 0xB6) },
            { "MessageSubtleForegroundOutColor", Color.FromArgb(0xFF, 0x6D, 0xC2, 0x64) },
            { "MessageHeaderForegroundColor", Color.FromArgb(0xFF, 0x15, 0x8D, 0xCD) },
            { "MessageHeaderForegroundOutColor", Color.FromArgb(0xFF, 0x3A, 0x8E, 0x26) },
            { "MessageHeaderBorderColor", Color.FromArgb(0xFF, 0x37, 0xA4, 0xDE) },
            { "MessageHeaderBorderOutColor", Color.FromArgb(0xFF, 0x5D, 0xC4, 0x52) },
            { "MessageMediaForegroundColor", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "MessageMediaForegroundOutColor", Color.FromArgb(0xFF, 0xF0, 0xFD, 0xDF) },
            { "MessageMediaBackgroundColor", Color.FromArgb(0xFF, 0x40, 0xA7, 0xE3) },
            { "MessageMediaBackgroundOutColor", Color.FromArgb(0xFF, 0x78, 0xC6, 0x7F) },
            { "MessageOverlayBackgroundColor", Color.FromArgb(0x54, 0x00, 0x00, 0x00) },
            { "MessageOverlayBackgroundOutColor", Color.FromArgb(0x54, 0x00, 0x00, 0x00) },
            { "MessageCallForegroundColor", Color.FromArgb(0xFF, 0x2A, 0xB3, 0x2A) },
            { "MessageCallForegroundOutColor", Color.FromArgb(0xFF, 0x2A, 0xB3, 0x2A) },
            { "MessageCallMissedForegroundColor", Color.FromArgb(0xFF, 0xDD, 0x58, 0x49) },
            { "MessageCallMissedForegroundOutColor", Color.FromArgb(0xFF, 0xDD, 0x58, 0x49) },
            { "SystemAltHighColor", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "SystemAltLowColor", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) },
            { "SystemAltMediumColor", Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) },
            { "SystemAltMediumHighColor", Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) },
            { "SystemAltMediumLowColor", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) },
            { "SystemBaseHighColor", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "SystemBaseLowColor", Color.FromArgb(0x33, 0x00, 0x00, 0x00) },
            { "SystemBaseMediumColor", Color.FromArgb(0x99, 0x00, 0x00, 0x00) },
            { "SystemBaseMediumHighColor", Color.FromArgb(0xCC, 0x00, 0x00, 0x00) },
            { "SystemBaseMediumLowColor", Color.FromArgb(0x66, 0x00, 0x00, 0x00) },
            { "SystemChromeAltLowColor", Color.FromArgb(0xFF, 0x17, 0x17, 0x17) },
            { "SystemChromeBlackHighColor", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "SystemChromeBlackLowColor", Color.FromArgb(0x33, 0x00, 0x00, 0x00) },
            { "SystemChromeBlackMediumLowColor", Color.FromArgb(0x66, 0x00, 0x00, 0x00) },
            { "SystemChromeBlackMediumColor", Color.FromArgb(0xCC, 0x00, 0x00, 0x00) },
            { "SystemChromeDisabledHighColor", Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC) },
            { "SystemChromeDisabledLowColor", Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A) },
            { "SystemChromeHighColor", Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC) },
            { "SystemChromeLowColor", Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2) },
            { "SystemChromeMediumColor", Color.FromArgb(0xFF, 0xE6, 0xE6, 0xE6) },
            { "SystemChromeMediumLowColor", Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2) },
            { "SystemChromeWhiteColor", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "SystemChromeGrayColor", Color.FromArgb(0xFF, 0x76, 0x76, 0x76) },
            { "SystemListLowColor", Color.FromArgb(0x19, 0x00, 0x00, 0x00) },
            { "SystemListMediumColor", Color.FromArgb(0x33, 0x00, 0x00, 0x00) },
            { "SystemControlBackgroundAccentBrush", "SystemAccentColor" },
            { "SystemControlBackgroundAltHighBrush", "SystemAltHighColor" },
            { "SystemControlBackgroundAltMediumHighBrush", "SystemAltMediumHighColor" },
            { "SystemControlBackgroundAltMediumBrush", "SystemAltMediumColor" },
            { "SystemControlBackgroundAltMediumLowBrush", "SystemAltMediumLowColor" },
            { "SystemControlBackgroundBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlBackgroundBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlBackgroundBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlBackgroundBaseMediumHighBrush", "SystemBaseMediumHighColor" },
            { "SystemControlBackgroundBaseMediumLowBrush", "SystemBaseMediumLowColor" },
            { "SystemControlBackgroundChromeBlackHighBrush", "SystemChromeBlackHighColor" },
            { "SystemControlBackgroundChromeBlackMediumBrush", "SystemChromeBlackMediumColor" },
            { "SystemControlBackgroundChromeBlackLowBrush", "SystemChromeBlackLowColor" },
            { "SystemControlBackgroundChromeBlackMediumLowBrush", "SystemChromeBlackMediumLowColor" },
            { "SystemControlBackgroundChromeMediumBrush", "SystemChromeMediumColor" },
            { "SystemControlBackgroundChromeMediumLowBrush", "SystemChromeMediumLowColor" },
            { "SystemControlBackgroundChromeWhiteBrush", "SystemChromeWhiteColor" },
            { "SystemControlBackgroundListLowBrush", "SystemListLowColor" },
            { "SystemControlBackgroundListMediumBrush", "SystemListMediumColor" },
            { "SystemControlDisabledAccentBrush", "SystemAccentColor" },
            { "SystemControlDisabledBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlDisabledBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlDisabledBaseMediumLowBrush", "SystemBaseMediumLowColor" },
            { "SystemControlDisabledChromeDisabledHighBrush", "SystemChromeDisabledHighColor" },
            { "SystemControlDisabledChromeDisabledLowBrush", "SystemChromeDisabledLowColor" },
            { "SystemControlDisabledChromeHighBrush", "SystemChromeHighColor" },
            { "SystemControlDisabledChromeMediumLowBrush", "SystemChromeMediumLowColor" },
            { "SystemControlDisabledListMediumBrush", "SystemListMediumColor" },
            { "SystemControlDisabledTransparentBrush", "Transparent" },
            { "SystemControlFocusVisualPrimaryBrush", "SystemBaseHighColor" },
            { "SystemControlFocusVisualSecondaryBrush", "SystemAltMediumColor" },
            { "SystemControlForegroundAccentBrush", "SystemAccentColor" },
            { "SystemControlForegroundAltHighBrush", "SystemAltHighColor" },
            { "SystemControlForegroundAltMediumHighBrush", "SystemAltMediumHighColor" },
            { "SystemControlForegroundBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlForegroundBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlForegroundBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlForegroundBaseMediumHighBrush", "SystemBaseMediumHighColor" },
            { "SystemControlForegroundBaseMediumLowBrush", "SystemBaseMediumLowColor" },
            { "SystemControlForegroundChromeBlackHighBrush", "SystemChromeBlackHighColor" },
            { "SystemControlForegroundChromeHighBrush", "SystemChromeHighColor" },
            { "SystemControlForegroundChromeMediumBrush", "SystemChromeMediumColor" },
            { "SystemControlForegroundChromeDisabledLowBrush", "SystemChromeDisabledLowColor" },
            { "SystemControlForegroundChromeWhiteBrush", "SystemChromeWhiteColor" },
            { "SystemControlForegroundChromeBlackMediumBrush", "SystemChromeBlackMediumColor" },
            { "SystemControlForegroundChromeBlackMediumLowBrush", "SystemChromeBlackMediumLowColor" },
            { "SystemControlForegroundChromeGrayBrush", "SystemChromeGrayColor" },
            { "SystemControlForegroundListLowBrush", "SystemListLowColor" },
            { "SystemControlForegroundListMediumBrush", "SystemListMediumColor" },
            { "SystemControlForegroundTransparentBrush", "Transparent" },
            { "SystemControlHighlightAccentBrush", "SystemAccentColor" },
            { "SystemControlHighlightAltAccentBrush", "SystemAccentColor" },
            { "SystemControlHighlightAltAltHighBrush", "SystemAltHighColor" },
            { "SystemControlHighlightAltBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlHighlightAltBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlHighlightAltBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlHighlightAltBaseMediumHighBrush", "SystemBaseMediumHighColor" },
            { "SystemControlHighlightAltAltMediumHighBrush", "SystemAltMediumHighColor" },
            { "SystemControlHighlightAltBaseMediumLowBrush", "SystemBaseMediumLowColor" },
            { "SystemControlHighlightAltListAccentHighBrush", "SystemAccentColor" },
            { "SystemControlHighlightAltListAccentLowBrush", "SystemAccentColor" },
            { "SystemControlHighlightAltListAccentMediumBrush", "SystemAccentColor" },
            { "SystemControlHighlightAltChromeWhiteBrush", "SystemChromeWhiteColor" },
            { "SystemControlHighlightAltTransparentBrush", "Transparent" },
            { "SystemControlHighlightBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlHighlightBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlHighlightBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlHighlightBaseMediumHighBrush", "SystemBaseMediumHighColor" },
            { "SystemControlHighlightBaseMediumLowBrush", "SystemBaseMediumLowColor" },
            { "SystemControlHighlightChromeAltLowBrush", "SystemChromeAltLowColor" },
            { "SystemControlHighlightChromeHighBrush", "SystemChromeHighColor" },
            { "SystemControlHighlightListAccentHighBrush", "SystemAccentColor" },
            { "SystemControlHighlightListAccentLowBrush", "SystemAccentColor" },
            { "SystemControlHighlightListAccentMediumBrush", "SystemAccentColor" },
            { "SystemControlHighlightListMediumBrush", "SystemListMediumColor" },
            { "SystemControlHighlightListLowBrush", "SystemListLowColor" },
            { "SystemControlHighlightChromeWhiteBrush", "SystemChromeWhiteColor" },
            { "SystemControlHighlightTransparentBrush", "Transparent" },
            { "SystemControlHyperlinkTextBrush", "SystemAccentColor" },
            { "SystemControlHyperlinkBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlHyperlinkBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlHyperlinkBaseMediumHighBrush", "SystemBaseMediumHighColor" },
            { "SystemControlPageBackgroundAltMediumBrush", "SystemAltMediumColor" },
            { "SystemControlPageBackgroundAltHighBrush", "SystemAltHighColor" },
            { "SystemControlPageBackgroundMediumAltMediumBrush", "SystemAltMediumColor" },
            { "SystemControlPageBackgroundBaseLowBrush", "SystemBaseLowColor" },
            { "SystemControlPageBackgroundBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlPageBackgroundListLowBrush", "SystemListLowColor" },
            { "SystemControlPageBackgroundChromeLowBrush", "SystemChromeLowColor" },
            { "SystemControlPageBackgroundChromeMediumLowBrush", "SystemChromeMediumLowColor" },
            { "SystemControlPageBackgroundTransparentBrush", "Transparent" },
            { "SystemControlPageTextBaseHighBrush", "SystemBaseHighColor" },
            { "SystemControlPageTextBaseMediumBrush", "SystemBaseMediumColor" },
            { "SystemControlPageTextChromeBlackMediumLowBrush", "SystemChromeBlackMediumLowColor" },
            { "SystemControlTransparentBrush", "Transparent" },
            { "SystemControlErrorTextForegroundBrush", "SystemErrorTextColor" },
            { "SystemControlTransientBorderBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "SystemControlDescriptionTextForegroundBrush", "SystemControlPageTextBaseMediumBrush" },
            { "SliderContainerBackground", "SystemControlTransparentBrush" },
            { "SliderContainerBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "SliderContainerBackgroundPressed", "SystemControlTransparentBrush" },
            { "SliderContainerBackgroundDisabled", "SystemControlTransparentBrush" },
            { "SliderThumbBackground", "SystemControlForegroundAccentBrush" },
            { "SliderThumbBackgroundPointerOver", "SystemControlHighlightChromeAltLowBrush" },
            { "SliderThumbBackgroundPressed", "SystemControlHighlightChromeHighBrush" },
            { "SliderThumbBackgroundDisabled", "SystemControlDisabledChromeDisabledHighBrush" },
            { "SliderTrackFill", "SystemControlForegroundBaseMediumLowBrush" },
            { "SliderTrackFillPointerOver", "SystemControlForegroundBaseMediumBrush" },
            { "SliderTrackFillPressed", "SystemControlForegroundBaseMediumLowBrush" },
            { "SliderTrackFillDisabled", "SystemControlDisabledChromeDisabledHighBrush" },
            { "SliderTrackValueFill", "SystemControlHighlightAccentBrush" },
            { "SliderTrackValueFillPointerOver", "SystemControlHighlightAccentBrush" },
            { "SliderTrackValueFillPressed", "SystemControlHighlightAccentBrush" },
            { "SliderTrackValueFillDisabled", "SystemControlDisabledChromeDisabledHighBrush" },
            { "SliderHeaderForeground", "SystemControlForegroundBaseHighBrush" },
            { "SliderHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "SliderTickBarFill", "SystemControlForegroundBaseMediumLowBrush" },
            { "SliderTickBarFillDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "SliderInlineTickBarFill", "SystemControlBackgroundAltHighBrush" },
            { "ButtonBackground", "SystemControlBackgroundBaseLowBrush" },
            { "ButtonBackgroundPointerOver", "SystemControlBackgroundBaseLowBrush" },
            { "ButtonBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "ButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "ButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "ButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "ButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "ButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "ButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "ButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "ButtonBorderBrushDisabled", "SystemControlDisabledTransparentBrush" },
            { "RadioButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "RadioButtonForegroundPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "RadioButtonForegroundPressed", "SystemControlForegroundBaseHighBrush" },
            { "RadioButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "RadioButtonBackground", "SystemControlTransparentBrush" },
            { "RadioButtonBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "RadioButtonBackgroundPressed", "SystemControlTransparentBrush" },
            { "RadioButtonBackgroundDisabled", "SystemControlTransparentBrush" },
            { "RadioButtonBorderBrush", "SystemControlTransparentBrush" },
            { "RadioButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "RadioButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "RadioButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "RadioButtonOuterEllipseStroke", "SystemControlForegroundBaseMediumHighBrush" },
            { "RadioButtonOuterEllipseStrokePointerOver", "SystemControlHighlightBaseHighBrush" },
            { "RadioButtonOuterEllipseStrokePressed", "SystemControlHighlightBaseMediumBrush" },
            { "RadioButtonOuterEllipseStrokeDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "RadioButtonOuterEllipseFill", "SystemControlTransparentBrush" },
            { "RadioButtonOuterEllipseFillPointerOver", "SystemControlTransparentBrush" },
            { "RadioButtonOuterEllipseFillPressed", "SystemControlTransparentBrush" },
            { "RadioButtonOuterEllipseFillDisabled", "SystemControlTransparentBrush" },
            { "RadioButtonOuterEllipseCheckedStroke", "SystemControlHighlightAccentBrush" },
            { "RadioButtonOuterEllipseCheckedStrokePointerOver", "SystemControlHighlightAccentBrush" },
            { "RadioButtonOuterEllipseCheckedStrokePressed", "SystemControlHighlightBaseMediumBrush" },
            { "RadioButtonOuterEllipseCheckedStrokeDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "RadioButtonOuterEllipseCheckedFill", "SystemControlHighlightAltTransparentBrush" },
            { "RadioButtonOuterEllipseCheckedFillPointerOver", "SystemControlHighlightTransparentBrush" },
            { "RadioButtonOuterEllipseCheckedFillPressed", "SystemControlHighlightTransparentBrush" },
            { "RadioButtonOuterEllipseCheckedFillDisabled", "SystemControlTransparentBrush" },
            { "RadioButtonCheckGlyphFill", "SystemControlHighlightBaseMediumHighBrush" },
            { "RadioButtonCheckGlyphFillPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "RadioButtonCheckGlyphFillPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "RadioButtonCheckGlyphFillDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "RadioButtonCheckGlyphStroke", "SystemControlTransparentBrush" },
            { "RadioButtonCheckGlyphStrokePointerOver", "SystemControlTransparentBrush" },
            { "RadioButtonCheckGlyphStrokePressed", "SystemControlTransparentBrush" },
            { "RadioButtonCheckGlyphStrokeDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxForegroundUnchecked", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundUncheckedPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundUncheckedPressed", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundUncheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxForegroundChecked", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundCheckedPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundCheckedPressed", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxForegroundIndeterminate", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundIndeterminatePointerOver", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundIndeterminatePressed", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxForegroundIndeterminateDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxBackgroundUnchecked", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundUncheckedPointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundUncheckedPressed", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundUncheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundChecked", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundCheckedPointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundCheckedPressed", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundCheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundIndeterminate", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundIndeterminatePointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundIndeterminatePressed", "SystemControlTransparentBrush" },
            { "CheckBoxBackgroundIndeterminateDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushUnchecked", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushUncheckedPointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushUncheckedPressed", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushUncheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushChecked", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushCheckedPointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushCheckedPressed", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushCheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushIndeterminate", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushIndeterminatePointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushIndeterminatePressed", "SystemControlTransparentBrush" },
            { "CheckBoxBorderBrushIndeterminateDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxCheckBackgroundStrokeUnchecked", "SystemControlForegroundBaseMediumHighBrush" },
            { "CheckBoxCheckBackgroundStrokeUncheckedPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "CheckBoxCheckBackgroundStrokeUncheckedPressed", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundStrokeUncheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxCheckBackgroundStrokeChecked", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "CheckBoxCheckBackgroundStrokeCheckedPressed", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundStrokeCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxCheckBackgroundStrokeIndeterminate", "SystemControlForegroundAccentBrush" },
            { "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver", "SystemControlHighlightAccentBrush" },
            { "CheckBoxCheckBackgroundStrokeIndeterminatePressed", "SystemControlHighlightBaseMediumBrush" },
            { "CheckBoxCheckBackgroundStrokeIndeterminateDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxCheckBackgroundFillUnchecked", "SystemControlTransparentBrush" },
            { "CheckBoxCheckBackgroundFillUncheckedPointerOver", "SystemControlTransparentBrush" },
            { "CheckBoxCheckBackgroundFillUncheckedPressed", "SystemControlBackgroundBaseMediumBrush" },
            { "CheckBoxCheckBackgroundFillUncheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxCheckBackgroundFillChecked", "SystemControlHighlightAccentBrush" },
            { "CheckBoxCheckBackgroundFillCheckedPointerOver", "SystemControlBackgroundAccentBrush" },
            { "CheckBoxCheckBackgroundFillCheckedPressed", "SystemControlHighlightBaseMediumBrush" },
            { "CheckBoxCheckBackgroundFillCheckedDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxCheckBackgroundFillIndeterminate", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundFillIndeterminatePointerOver", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundFillIndeterminatePressed", "SystemControlHighlightTransparentBrush" },
            { "CheckBoxCheckBackgroundFillIndeterminateDisabled", "SystemControlTransparentBrush" },
            { "CheckBoxCheckGlyphForegroundUnchecked", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundUncheckedPointerOver", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundUncheckedPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundUncheckedDisabled", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundChecked", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundCheckedPointerOver", "SystemControlForegroundChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundCheckedPressed", "SystemControlForegroundChromeWhiteBrush" },
            { "CheckBoxCheckGlyphForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CheckBoxCheckGlyphForegroundIndeterminate", "SystemControlForegroundBaseMediumHighBrush" },
            { "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "SystemControlForegroundBaseHighBrush" },
            { "CheckBoxCheckGlyphForegroundIndeterminatePressed", "SystemControlForegroundBaseMediumBrush" },
            { "CheckBoxCheckGlyphForegroundIndeterminateDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "HyperlinkButtonForeground", "SystemControlHyperlinkTextBrush" },
            { "HyperlinkButtonForegroundPointerOver", "SystemControlPageTextBaseMediumBrush" },
            { "HyperlinkButtonForegroundPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "HyperlinkButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "HyperlinkButtonBackground", "SystemControlPageBackgroundTransparentBrush" },
            { "HyperlinkButtonBackgroundPointerOver", "SystemControlPageBackgroundTransparentBrush" },
            { "HyperlinkButtonBackgroundPressed", "SystemControlPageBackgroundTransparentBrush" },
            { "HyperlinkButtonBackgroundDisabled", "SystemControlPageBackgroundTransparentBrush" },
            { "HyperlinkButtonBorderBrush", "SystemControlTransparentBrush" },
            { "HyperlinkButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "HyperlinkButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "HyperlinkButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "RepeatButtonBackground", "SystemControlBackgroundBaseLowBrush" },
            { "RepeatButtonBackgroundPointerOver", "SystemControlBackgroundBaseLowBrush" },
            { "RepeatButtonBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "RepeatButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "RepeatButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "RepeatButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "RepeatButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "RepeatButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "RepeatButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "RepeatButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "RepeatButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "RepeatButtonBorderBrushDisabled", "SystemControlDisabledTransparentBrush" },
            { "ToggleSwitchContentForeground", "SystemControlForegroundBaseHighBrush" },
            { "ToggleSwitchContentForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleSwitchHeaderForeground", "SystemControlForegroundBaseHighBrush" },
            { "ToggleSwitchHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleSwitchContainerBackground", "SystemControlTransparentBrush" },
            { "ToggleSwitchContainerBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "ToggleSwitchContainerBackgroundPressed", "SystemControlTransparentBrush" },
            { "ToggleSwitchContainerBackgroundDisabled", "SystemControlTransparentBrush" },
            { "ToggleSwitchFillOff", "SystemControlTransparentBrush" },
            { "ToggleSwitchFillOffPointerOver", "SystemControlTransparentBrush" },
            { "ToggleSwitchFillOffPressed", "SystemControlHighlightBaseMediumBrush" },
            { "ToggleSwitchFillOffDisabled", "SystemControlTransparentBrush" },
            { "ToggleSwitchStrokeOff", "SystemControlForegroundBaseMediumHighBrush" },
            { "ToggleSwitchStrokeOffPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "ToggleSwitchStrokeOffPressed", "SystemControlForegroundBaseMediumHighBrush" },
            { "ToggleSwitchStrokeOffDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleSwitchFillOn", "SystemControlHighlightAccentBrush" },
            { "ToggleSwitchFillOnPointerOver", "SystemControlHighlightAltListAccentHighBrush" },
            { "ToggleSwitchFillOnPressed", "SystemControlHighlightBaseMediumBrush" },
            { "ToggleSwitchFillOnDisabled", "SystemControlDisabledBaseLowBrush" },
            { "ToggleSwitchStrokeOn", "SystemControlHighlightBaseHighBrush" },
            { "ToggleSwitchStrokeOnPointerOver", "SystemControlHighlightListAccentHighBrush" },
            { "ToggleSwitchStrokeOnPressed", "SystemControlHighlightBaseMediumBrush" },
            { "ToggleSwitchStrokeOnDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleSwitchKnobFillOff", "SystemControlForegroundBaseMediumHighBrush" },
            { "ToggleSwitchKnobFillOffPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "ToggleSwitchKnobFillOffPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleSwitchKnobFillOffDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleSwitchKnobFillOn", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleSwitchKnobFillOnPointerOver", "SystemControlHighlightChromeWhiteBrush" },
            { "ToggleSwitchKnobFillOnPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleSwitchKnobFillOnDisabled", "SystemControlPageBackgroundBaseLowBrush" },
            { "ThumbBackground", "SystemControlBackgroundBaseLowBrush" },
            { "ThumbBackgroundPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "ThumbBackgroundPressed", "SystemControlHighlightBaseMediumBrush" },
            { "ThumbBorderBrush", "SystemControlHighlightTransparentBrush" },
            { "ThumbBorderBrushPointerOver", "SystemControlHighlightTransparentBrush" },
            { "ThumbBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "ToggleButtonBackground", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundPointerOver", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "ToggleButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundChecked", "SystemControlHighlightAccentBrush" },
            { "ToggleButtonBackgroundCheckedPointerOver", "SystemControlHighlightAccentBrush" },
            { "ToggleButtonBackgroundCheckedPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "ToggleButtonBackgroundCheckedDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundIndeterminate", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundIndeterminatePointerOver", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonBackgroundIndeterminatePressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "ToggleButtonBackgroundIndeterminateDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "ToggleButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "ToggleButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "ToggleButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "ToggleButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleButtonForegroundChecked", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleButtonForegroundCheckedPointerOver", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleButtonForegroundCheckedPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ToggleButtonForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleButtonForegroundIndeterminate", "SystemControlForegroundBaseHighBrush" },
            { "ToggleButtonForegroundIndeterminatePointerOver", "SystemControlHighlightBaseHighBrush" },
            { "ToggleButtonForegroundIndeterminatePressed", "SystemControlHighlightBaseHighBrush" },
            { "ToggleButtonForegroundIndeterminateDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "ToggleButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "ToggleButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "ToggleButtonBorderBrushDisabled", "SystemControlDisabledTransparentBrush" },
            { "ToggleButtonBorderBrushChecked", "SystemControlHighlightAltTransparentBrush" },
            { "ToggleButtonBorderBrushCheckedPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "ToggleButtonBorderBrushCheckedPressed", "SystemControlTransparentBrush" },
            { "ToggleButtonBorderBrushCheckedDisabled", "SystemControlDisabledTransparentBrush" },
            { "ToggleButtonBorderBrushIndeterminate", "SystemControlForegroundTransparentBrush" },
            { "ToggleButtonBorderBrushIndeterminatePointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "ToggleButtonBorderBrushIndeterminatePressed", "SystemControlHighlightTransparentBrush" },
            { "ToggleButtonBorderBrushIndeterminateDisabled", "SystemControlDisabledTransparentBrush" },
            { "ScrollBarBackground", "SystemControlTransparentBrush" },
            { "ScrollBarBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "ScrollBarBackgroundDisabled", "SystemControlTransparentBrush" },
            { "ScrollBarForeground", "SystemControlTransparentBrush" },
            { "ScrollBarBorderBrush", "SystemControlTransparentBrush" },
            { "ScrollBarBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "ScrollBarBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBackground", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBackgroundPointerOver", "SystemControlBackgroundListLowBrush" },
            { "ScrollBarButtonBackgroundPressed", "SystemControlBackgroundBaseMediumBrush" },
            { "ScrollBarButtonBackgroundDisabled", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBorderBrush", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "ScrollBarButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "ScrollBarButtonArrowForeground", "SystemControlForegroundBaseHighBrush" },
            { "ScrollBarButtonArrowForegroundPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "ScrollBarButtonArrowForegroundPressed", "SystemControlForegroundAltHighBrush" },
            { "ScrollBarButtonArrowForegroundDisabled", "SystemControlForegroundBaseLowBrush" },
            { "ScrollBarThumbFill", "SystemControlForegroundChromeDisabledLowBrush" },
            { "ScrollBarThumbFillPointerOver", "SystemControlBackgroundBaseMediumLowBrush" },
            { "ScrollBarThumbFillPressed", "SystemControlBackgroundBaseMediumBrush" },
            { "ScrollBarThumbFillDisabled", "SystemControlDisabledTransparentBrush" },
            { "ScrollBarTrackFill", "SystemChromeMediumColor" },
            { "ScrollBarTrackFillPointerOver", "SystemChromeMediumColor" },
            { "ScrollBarTrackFillDisabled", "SystemControlDisabledTransparentBrush" },
            { "ScrollBarTrackStroke", "SystemControlForegroundTransparentBrush" },
            { "ScrollBarTrackStrokePointerOver", "SystemControlForegroundTransparentBrush" },
            { "ScrollBarTrackStrokeDisabled", "SystemControlDisabledTransparentBrush" },
            { "ScrollBarPanningThumbBackgroundDisabled", "SystemControlDisabledChromeHighBrush" },
            { "ScrollBarThumbBackgroundColor", "SystemBaseLowColor" },
            { "ScrollBarPanningThumbBackgroundColor", "SystemChromeDisabledLowColor" },
            { "ScrollBarThumbBackground", "ScrollBarThumbBackgroundColor" },
            { "ScrollBarPanningThumbBackground", "ScrollBarPanningThumbBackgroundColor" },
            { "ScrollViewerScrollBarSeparatorBackground", "SystemChromeMediumColor" },
            { "ListViewHeaderItemBackground", "SystemControlTransparentBrush" },
            { "ListViewHeaderItemDividerStroke", "SystemControlForegroundBaseLowBrush" },
            { "ComboBoxItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "ComboBoxItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ComboBoxItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundSelectedUnfocused", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundSelectedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundSelectedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxItemForegroundSelectedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ComboBoxItemBackground", "SystemControlTransparentBrush" },
            { "ComboBoxItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "ComboBoxItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "ComboBoxItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "ComboBoxItemBackgroundSelected", "SystemControlHighlightListAccentLowBrush" },
            { "ComboBoxItemBackgroundSelectedUnfocused", "SystemControlHighlightListAccentLowBrush" },
            { "ComboBoxItemBackgroundSelectedPressed", "SystemControlHighlightListAccentHighBrush" },
            { "ComboBoxItemBackgroundSelectedPointerOver", "SystemControlHighlightListAccentMediumBrush" },
            { "ComboBoxItemBackgroundSelectedDisabled", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrush", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushPressed", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushSelected", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushSelectedUnfocused", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushSelectedPressed", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushSelectedPointerOver", "SystemControlTransparentBrush" },
            { "ComboBoxItemBorderBrushSelectedDisabled", "SystemControlTransparentBrush" },
            { "ComboBoxBackground", "SystemControlBackgroundAltMediumLowBrush" },
            { "ComboBoxBackgroundPointerOver", "SystemControlPageBackgroundAltMediumBrush" },
            { "ComboBoxBackgroundPressed", "SystemControlBackgroundListMediumBrush" },
            { "ComboBoxBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "ComboBoxBackgroundUnfocused", "SystemControlHighlightListAccentLowBrush" },
            { "ComboBoxBackgroundBorderBrushFocused", "SystemControlHighlightTransparentBrush" },
            { "ComboBoxBackgroundBorderBrushUnfocused", "SystemControlHighlightBaseMediumLowBrush" },
            { "ComboBoxForeground", "SystemControlForegroundBaseHighBrush" },
            { "ComboBoxForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ComboBoxForegroundFocused", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxForegroundFocusedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxPlaceHolderForeground", "SystemControlPageTextBaseHighBrush" },
            { "ComboBoxPlaceHolderForegroundFocusedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ComboBoxBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "ComboBoxBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "ComboBoxBorderBrushPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "ComboBoxBorderBrushDisabled", "SystemControlDisabledBaseLowBrush" },
            { "ComboBoxDropDownBackgroundPointerOver", "SystemControlBackgroundListLowBrush" },
            { "ComboBoxDropDownBackgroundPointerPressed", "SystemControlBackgroundListMediumBrush" },
            { "ComboBoxFocusedDropDownBackgroundPointerOver", "SystemControlBackgroundChromeBlackLowBrush" },
            { "ComboBoxFocusedDropDownBackgroundPointerPressed", "SystemControlBackgroundChromeBlackMediumLowBrush" },
            { "ComboBoxDropDownGlyphForeground", "SystemControlForegroundBaseMediumHighBrush" },
            { "ComboBoxEditableDropDownGlyphForeground", "SystemControlForegroundBaseMediumHighBrush" },
            { "ComboBoxDropDownGlyphForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ComboBoxDropDownGlyphForegroundFocused", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "ComboBoxDropDownGlyphForegroundFocusedPressed", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "ComboBoxDropDownBackground", "SystemControlTransientBackgroundBrush" },
            { "ComboBoxDropDownForeground", "SystemControlForegroundBaseHighBrush" },
            { "ComboBoxDropDownBorderBrush", "SystemControlTransientBorderBrush" },
            { "AppBarSeparatorForeground", "SystemControlForegroundBaseMediumLowBrush" },
            { "AppBarEllipsisButtonBackground", "SystemControlTransparentBrush" },
            { "AppBarEllipsisButtonBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "AppBarEllipsisButtonBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "AppBarEllipsisButtonBackgroundDisabled", "SystemControlTransparentBrush" },
            { "AppBarEllipsisButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "AppBarEllipsisButtonForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarEllipsisButtonForegroundPressed", "SystemControlForegroundBaseHighBrush" },
            { "AppBarEllipsisButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarEllipsisButtonBorderBrush", "SystemControlTransparentBrush" },
            { "AppBarEllipsisButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "AppBarEllipsisButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "AppBarEllipsisButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "AppBarBackground", "SystemControlBackgroundChromeMediumBrush" },
            { "AppBarForeground", "SystemControlForegroundBaseHighBrush" },
            { "AppBarHighContrastBorder", "SystemControlForegroundTransparentBrush" },
            { "ContentDialogForeground", "SystemControlPageTextBaseHighBrush" },
            { "ContentDialogBackground", "SystemControlPageBackgroundAltHighBrush" },
            { "ContentDialogBorderBrush", "SystemControlBackgroundBaseLowBrush" },
            { "AccentButtonBackground", "SystemControlForegroundAccentBrush" },
            { "AccentButtonBackgroundPointerOver", "SystemControlForegroundAccentBrush" },
            { "AccentButtonBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "AccentButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "AccentButtonForeground", "SystemControlBackgroundChromeWhiteBrush" },
            { "AccentButtonForegroundPointerOver", "SystemControlBackgroundChromeWhiteBrush" },
            { "AccentButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "AccentButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AccentButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "AccentButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "AccentButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "AccentButtonBorderBrushDisabled", "SystemControlDisabledTransparentBrush" },
            { "ToolTipForeground", "SystemControlForegroundBaseHighBrush" },
            { "ToolTipBackground", "SystemControlBackgroundChromeMediumLowBrush" },
            { "ToolTipBorderBrush", "SystemControlTransientBorderBrush" },
            { "CalendarDatePickerForeground", "SystemControlForegroundBaseHighBrush" },
            { "CalendarDatePickerForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarDatePickerCalendarGlyphForeground", "SystemControlForegroundBaseMediumHighBrush" },
            { "CalendarDatePickerCalendarGlyphForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarDatePickerTextForeground", "SystemControlForegroundBaseMediumBrush" },
            { "CalendarDatePickerTextForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarDatePickerTextForegroundSelected", "SystemControlForegroundBaseHighBrush" },
            { "CalendarDatePickerHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarDatePickerBackground", "SystemControlBackgroundAltMediumLowBrush" },
            { "CalendarDatePickerBackgroundPointerOver", "SystemControlPageBackgroundAltMediumBrush" },
            { "CalendarDatePickerBackgroundPressed", "SystemControlBackgroundBaseLowBrush" },
            { "CalendarDatePickerBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "CalendarDatePickerBackgroundFocused", "SystemControlHighlightListAccentLowBrush" },
            { "CalendarDatePickerBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "CalendarDatePickerBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "CalendarDatePickerBorderBrushPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "CalendarDatePickerBorderBrushDisabled", "SystemControlDisabledBaseLowBrush" },
            { "CalendarViewFocusBorderBrush", "SystemControlForegroundBaseHighBrush" },
            { "CalendarViewSelectedHoverBorderBrush", "SystemControlHighlightListAccentMediumBrush" },
            { "CalendarViewSelectedPressedBorderBrush", "SystemControlHighlightListAccentHighBrush" },
            { "CalendarViewSelectedBorderBrush", "SystemControlHighlightAccentBrush" },
            { "CalendarViewHoverBorderBrush", "SystemControlHighlightBaseMediumLowBrush" },
            { "CalendarViewPressedBorderBrush", "SystemControlHighlightBaseMediumBrush" },
            { "CalendarViewTodayForeground", "SystemControlHighlightAltChromeWhiteBrush" },
            { "CalendarViewBlackoutForeground", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarViewSelectedForeground", "SystemControlHighlightBaseHighBrush" },
            { "CalendarViewPressedForeground", "SystemControlHighlightBaseHighBrush" },
            { "CalendarViewOutOfScopeForeground", "SystemControlHyperlinkBaseHighBrush" },
            { "CalendarViewCalendarItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "CalendarViewOutOfScopeBackground", "SystemControlDisabledChromeMediumLowBrush" },
            { "CalendarViewCalendarItemBackground", "SystemControlBackgroundAltHighBrush" },
            { "CalendarViewForeground", "SystemControlHyperlinkBaseMediumHighBrush" },
            { "CalendarViewBackground", "SystemControlBackgroundAltHighBrush" },
            { "CalendarViewBorderBrush", "SystemControlForegroundChromeMediumBrush" },
            { "CalendarViewWeekDayForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarViewNavigationButtonBackground", "SystemControlTransparentBrush" },
            { "CalendarViewNavigationButtonForegroundPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "CalendarViewNavigationButtonForegroundPressed", "SystemControlForegroundBaseMediumBrush" },
            { "CalendarViewNavigationButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CalendarViewNavigationButtonBorderBrushPointerOver", "SystemControlHighlightTransparentBrush" },
            { "CalendarViewNavigationButtonBorderBrush", "SystemControlTransparentBrush" },
            { "HubForeground", "SystemControlPageTextBaseHighBrush" },
            { "HubSectionHeaderButtonForeground", "SystemControlHyperlinkTextBrush" },
            { "HubSectionHeaderButtonForegroundPointerOver", "SystemControlHyperlinkBaseMediumBrush" },
            { "HubSectionHeaderButtonForegroundPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "HubSectionHeaderButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "HubSectionHeaderForeground", "SystemControlPageTextBaseHighBrush" },
            { "FlipViewBackground", "SystemControlPageBackgroundListLowBrush" },
            { "FlipViewNextPreviousButtonBackground", "SystemControlBackgroundBaseMediumLowBrush" },
            { "FlipViewNextPreviousButtonBackgroundPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "FlipViewNextPreviousButtonBackgroundPressed", "SystemControlHighlightBaseMediumHighBrush" },
            { "FlipViewNextPreviousArrowForeground", "SystemControlForegroundAltMediumHighBrush" },
            { "FlipViewNextPreviousArrowForegroundPointerOver", "SystemControlHighlightAltAltMediumHighBrush" },
            { "FlipViewNextPreviousArrowForegroundPressed", "SystemControlHighlightAltAltMediumHighBrush" },
            { "FlipViewNextPreviousButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "FlipViewNextPreviousButtonBorderBrushPointerOver", "SystemControlForegroundTransparentBrush" },
            { "FlipViewNextPreviousButtonBorderBrushPressed", "SystemControlForegroundTransparentBrush" },
            { "FlipViewItemBackground", "SystemControlTransparentBrush" },
            { "DateTimePickerFlyoutButtonBackground", "SystemControlTransparentBrush" },
            { "DateTimePickerFlyoutButtonBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "DateTimePickerFlyoutButtonBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "DateTimePickerFlyoutButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "DateTimePickerFlyoutButtonBorderBrushPointerOver", "SystemControlHighlightTransparentBrush" },
            { "DateTimePickerFlyoutButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "DateTimePickerFlyoutButtonForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "DateTimePickerFlyoutButtonForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "DatePickerSpacerFill", "SystemControlForegroundBaseLowBrush" },
            { "DatePickerSpacerFillDisabled", "SystemControlDisabledBaseLowBrush" },
            { "DatePickerHeaderForeground", "SystemControlForegroundBaseHighBrush" },
            { "DatePickerHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "DatePickerButtonBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "DatePickerButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "DatePickerButtonBorderBrushPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "DatePickerButtonBorderBrushDisabled", "SystemControlDisabledBaseLowBrush" },
            { "DatePickerButtonBackground", "SystemControlBackgroundAltMediumLowBrush" },
            { "DatePickerButtonBackgroundPointerOver", "SystemControlPageBackgroundAltMediumBrush" },
            { "DatePickerButtonBackgroundPressed", "SystemControlBackgroundBaseLowBrush" },
            { "DatePickerButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "DatePickerButtonBackgroundFocused", "SystemControlHighlightListAccentLowBrush" },
            { "DatePickerButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "DatePickerButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "DatePickerButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "DatePickerButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "DatePickerButtonForegroundFocused", "SystemControlHighlightAltBaseHighBrush" },
            { "DatePickerFlyoutPresenterBackground", "SystemControlTransientBackgroundBrush" },
            { "DatePickerFlyoutPresenterBorderBrush", "SystemControlTransientBorderBrush" },
            { "DatePickerFlyoutPresenterSpacerFill", "SystemControlForegroundBaseLowBrush" },
            { "DatePickerFlyoutPresenterHighlightFill", "SystemControlHighlightListAccentLowBrush" },
            { "TimePickerSpacerFill", "SystemControlForegroundBaseLowBrush" },
            { "TimePickerSpacerFillDisabled", "SystemControlDisabledBaseLowBrush" },
            { "TimePickerHeaderForeground", "SystemControlForegroundBaseHighBrush" },
            { "TimePickerHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TimePickerButtonBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "TimePickerButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "TimePickerButtonBorderBrushPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "TimePickerButtonBorderBrushDisabled", "SystemControlDisabledBaseLowBrush" },
            { "TimePickerButtonBackground", "SystemControlBackgroundAltMediumLowBrush" },
            { "TimePickerButtonBackgroundPointerOver", "SystemControlPageBackgroundAltMediumBrush" },
            { "TimePickerButtonBackgroundPressed", "SystemControlBackgroundBaseLowBrush" },
            { "TimePickerButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "TimePickerButtonBackgroundFocused", "SystemControlHighlightListAccentLowBrush" },
            { "TimePickerButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "TimePickerButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "TimePickerButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "TimePickerButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TimePickerButtonForegroundFocused", "SystemControlHighlightAltBaseHighBrush" },
            { "TimePickerFlyoutPresenterBackground", "SystemControlTransientBackgroundBrush" },
            { "TimePickerFlyoutPresenterBorderBrush", "SystemControlTransientBorderBrush" },
            { "TimePickerFlyoutPresenterSpacerFill", "SystemControlForegroundBaseLowBrush" },
            { "TimePickerFlyoutPresenterHighlightFill", "SystemControlHighlightListAccentLowBrush" },
            { "LoopingSelectorButtonBackground", "SystemControlBackgroundChromeMediumBrush" },
            { "LoopingSelectorItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "LoopingSelectorItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "LoopingSelectorItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "LoopingSelectorItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "LoopingSelectorItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "LoopingSelectorItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "TextControlForeground", "SystemControlForegroundBaseHighBrush" },
            { "TextControlForegroundPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "TextControlForegroundFocused", "SystemControlForegroundChromeBlackHighBrush" },
            { "TextControlForegroundDisabled", "SystemControlDisabledChromeDisabledLowBrush" },
            { "TextControlBackground", "SystemControlBackgroundAltMediumLowBrush" },
            { "TextControlBackgroundPointerOver", "SystemControlBackgroundAltMediumBrush" },
            { "TextControlBackgroundFocused", "SystemControlBackgroundChromeWhiteBrush" },
            { "TextControlBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "TextControlBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "TextControlBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "TextControlBorderBrushFocused", "SystemControlHighlightAccentBrush" },
            { "TextControlBorderBrushDisabled", "SystemControlDisabledBaseLowBrush" },
            { "TextControlPlaceholderForeground", "SystemControlPageTextBaseMediumBrush" },
            { "TextControlPlaceholderForegroundPointerOver", "SystemControlPageTextBaseMediumBrush" },
            { "TextControlPlaceholderForegroundFocused", "SystemControlPageTextChromeBlackMediumLowBrush" },
            { "TextControlPlaceholderForegroundDisabled", "SystemControlDisabledChromeDisabledLowBrush" },
            { "TextControlHeaderForeground", "SystemControlForegroundBaseHighBrush" },
            { "TextControlHeaderForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TextControlSelectionHighlightColor", "SystemControlHighlightAccentBrush" },
            { "TextControlButtonBackground", "SystemControlTransparentBrush" },
            { "TextControlButtonBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "TextControlButtonBackgroundPressed", "SystemControlHighlightAccentBrush" },
            { "TextControlButtonBorderBrush", "SystemControlTransparentBrush" },
            { "TextControlButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "TextControlButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "TextControlButtonForeground", "SystemControlForegroundChromeBlackMediumBrush" },
            { "TextControlButtonForegroundPointerOver", "SystemControlHighlightAccentBrush" },
            { "TextControlButtonForegroundPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ContentLinkForegroundColor", "SystemControlHyperlinkTextBrush" },
            { "ContentLinkBackgroundColor", "SystemControlPageBackgroundChromeLowBrush" },
            { "TextControlHighlighterForeground", "SystemControlForegroundBaseHighBrush" },
            { "TextControlHighlighterBackground", Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00) },
            { "FlyoutPresenterBackground", "SystemControlTransientBackgroundBrush" },
            { "FlyoutBorderThemeBrush", "SystemControlTransientBorderBrush" },
            { "ToggleMenuFlyoutItemBackground", "SystemControlTransparentBrush" },
            { "ToggleMenuFlyoutItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "ToggleMenuFlyoutItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "ToggleMenuFlyoutItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "ToggleMenuFlyoutItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "ToggleMenuFlyoutItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "ToggleMenuFlyoutItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ToggleMenuFlyoutItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleMenuFlyoutItemKeyboardAcceleratorTextForeground", "SystemControlForegroundBaseMediumBrush" },
            { "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "SystemControlHighlightAltBaseMediumBrush" },
            { "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "ToggleMenuFlyoutItemCheckGlyphForeground", "SystemControlForegroundBaseMediumHighBrush" },
            { "ToggleMenuFlyoutItemCheckGlyphForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "ToggleMenuFlyoutItemCheckGlyphForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "ToggleMenuFlyoutItemCheckGlyphForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "PivotBackground", "SystemControlTransparentBrush" },
            { "PivotHeaderBackground", "SystemControlTransparentBrush" },
            { "PivotNextButtonBackground", "SystemControlBackgroundBaseMediumLowBrush" },
            { "PivotNextButtonBackgroundPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "PivotNextButtonBackgroundPressed", "SystemControlHighlightBaseMediumHighBrush" },
            { "PivotNextButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "PivotNextButtonBorderBrushPointerOver", "SystemControlForegroundTransparentBrush" },
            { "PivotNextButtonBorderBrushPressed", "SystemControlForegroundTransparentBrush" },
            { "PivotNextButtonForeground", "SystemControlForegroundAltMediumHighBrush" },
            { "PivotNextButtonForegroundPointerOver", "SystemControlHighlightAltAltMediumHighBrush" },
            { "PivotNextButtonForegroundPressed", "SystemControlHighlightAltAltMediumHighBrush" },
            { "PivotPreviousButtonBackground", "SystemControlBackgroundBaseMediumLowBrush" },
            { "PivotPreviousButtonBackgroundPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "PivotPreviousButtonBackgroundPressed", "SystemControlHighlightBaseMediumHighBrush" },
            { "PivotPreviousButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "PivotPreviousButtonBorderBrushPointerOver", "SystemControlForegroundTransparentBrush" },
            { "PivotPreviousButtonBorderBrushPressed", "SystemControlForegroundTransparentBrush" },
            { "PivotPreviousButtonForeground", "SystemControlForegroundAltMediumHighBrush" },
            { "PivotPreviousButtonForegroundPointerOver", "SystemControlHighlightAltAltMediumHighBrush" },
            { "PivotPreviousButtonForegroundPressed", "SystemControlHighlightAltAltMediumHighBrush" },
            { "PivotItemBackground", "SystemControlTransparentBrush" },
            { "PivotHeaderItemBackgroundUnselected", "SystemControlTransparentBrush" },
            { "PivotHeaderItemBackgroundUnselectedPointerOver", "SystemControlHighlightTransparentBrush" },
            { "PivotHeaderItemBackgroundUnselectedPressed", "SystemControlHighlightTransparentBrush" },
            { "PivotHeaderItemBackgroundSelected", "SystemControlHighlightTransparentBrush" },
            { "PivotHeaderItemBackgroundSelectedPointerOver", "SystemControlHighlightTransparentBrush" },
            { "PivotHeaderItemBackgroundSelectedPressed", "SystemControlHighlightTransparentBrush" },
            { "PivotHeaderItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "PivotHeaderItemForegroundUnselected", "SystemControlForegroundBaseMediumBrush" },
            { "PivotHeaderItemForegroundUnselectedPointerOver", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "PivotHeaderItemForegroundUnselectedPressed", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "PivotHeaderItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "PivotHeaderItemForegroundSelectedPointerOver", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "PivotHeaderItemForegroundSelectedPressed", "SystemControlHighlightAltBaseMediumHighBrush" },
            { "PivotHeaderItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "PivotHeaderItemFocusPipeFill", "SystemControlHighlightAltAccentBrush" },
            { "PivotHeaderItemSelectedPipeFill", "SystemControlHighlightAltAccentBrush" },
            { "GridViewHeaderItemBackground", "SystemControlTransparentBrush" },
            { "GridViewHeaderItemDividerStroke", "SystemControlForegroundBaseLowBrush" },
            { "GridViewItemBackground", "SystemControlTransparentBrush" },
            { "GridViewItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "GridViewItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "GridViewItemBackgroundSelected", "SystemControlHighlightAccentBrush" },
            { "GridViewItemBackgroundSelectedPointerOver", "SystemControlHighlightListAccentMediumBrush" },
            { "GridViewItemBackgroundSelectedPressed", "SystemControlHighlightListAccentHighBrush" },
            { "GridViewItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "GridViewItemForegroundPointerOver", "SystemControlForegroundBaseHighBrush" },
            { "GridViewItemForegroundSelected", "SystemControlForegroundBaseHighBrush" },
            { "GridViewItemFocusVisualPrimaryBrush", "SystemControlFocusVisualPrimaryBrush" },
            { "GridViewItemFocusVisualSecondaryBrush", "SystemControlFocusVisualSecondaryBrush" },
            { "GridViewItemFocusBorderBrush", "SystemControlForegroundAltHighBrush" },
            { "GridViewItemFocusSecondaryBorderBrush", "SystemControlForegroundBaseHighBrush" },
            { "GridViewItemCheckBrush", "SystemControlForegroundBaseMediumHighBrush" },
            { "GridViewItemCheckBoxBrush", "SystemControlBackgroundChromeMediumBrush" },
            { "GridViewItemDragBackground", "SystemControlTransparentBrush" },
            { "GridViewItemDragForeground", "SystemControlHighlightAltChromeWhiteBrush" },
            { "GridViewItemPlaceholderBackground", "SystemControlDisabledChromeDisabledHighBrush" },
            { "MediaTransportControlsPanelBackground", "SystemControlPageBackgroundAltMediumBrush" },
            { "MediaTransportControlsFlyoutBackground", "SystemControlTransientBackgroundBrush" },
            { "AppBarLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "CalendarDatePickerLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "ComboBoxLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "DatePickerLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "FlyoutLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "PopupLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "SplitViewLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "TimePickerLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "JumpListDefaultEnabledBackground", "SystemControlBackgroundAccentBrush" },
            { "JumpListDefaultEnabledForeground", "SystemControlForegroundChromeWhiteBrush" },
            { "JumpListDefaultDisabledBackground", "SystemControlBackgroundBaseLowBrush" },
            { "JumpListDefaultDisabledForeground", "SystemControlDisabledBaseMediumLowBrush" },
            { "KeyTipForeground", "SystemControlBackgroundChromeWhiteBrush" },
            { "KeyTipBackground", "SystemControlForegroundChromeGrayBrush" },
            { "KeyTipBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "SystemChromeAltMediumHighColor", Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) },
            { "SystemChromeAltHighColor", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealAltHighColor", Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealAltLowColor", Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealAltMediumColor", Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealAltMediumHighColor", Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealAltMediumLowColor", Color.FromArgb(0x61, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealBaseHighColor", Color.FromArgb(0xE6, 0x00, 0x00, 0x00) },
            { "SystemRevealBaseLowColor", Color.FromArgb(0x2E, 0x00, 0x00, 0x00) },
            { "SystemRevealBaseMediumColor", Color.FromArgb(0x8A, 0x00, 0x00, 0x00) },
            { "SystemRevealBaseMediumHighColor", Color.FromArgb(0xB8, 0x00, 0x00, 0x00) },
            { "SystemRevealBaseMediumLowColor", Color.FromArgb(0x61, 0x00, 0x00, 0x00) },
            { "SystemRevealChromeAltLowColor", Color.FromArgb(0xE6, 0x17, 0x17, 0x17) },
            { "SystemRevealChromeBlackHighColor", Color.FromArgb(0xE6, 0x00, 0x00, 0x00) },
            { "SystemRevealChromeBlackLowColor", Color.FromArgb(0x2E, 0x00, 0x00, 0x00) },
            { "SystemRevealChromeBlackMediumLowColor", Color.FromArgb(0x66, 0x00, 0x00, 0x00) },
            { "SystemRevealChromeBlackMediumColor", Color.FromArgb(0xB8, 0x00, 0x00, 0x00) },
            { "SystemRevealChromeHighColor", Color.FromArgb(0xE6, 0xCC, 0xCC, 0xCC) },
            { "SystemRevealChromeLowColor", Color.FromArgb(0xE6, 0xF2, 0xF2, 0xF2) },
            { "SystemRevealChromeMediumColor", Color.FromArgb(0xE6, 0xE6, 0xE6, 0xE6) },
            { "SystemRevealChromeMediumLowColor", Color.FromArgb(0xE6, 0xF2, 0xF2, 0xF2) },
            { "SystemRevealChromeWhiteColor", Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) },
            { "SystemRevealChromeGrayColor", Color.FromArgb(0xE6, 0x76, 0x76, 0x76) },
            { "SystemRevealListLowColor", Color.FromArgb(0x17, 0x00, 0x00, 0x00) },
            { "SystemRevealListMediumColor", Color.FromArgb(0x2E, 0x00, 0x00, 0x00) },
            { "MenuFlyoutPresenterBackground", "SystemControlTransientBackgroundBrush" },
            { "MenuFlyoutPresenterBorderBrush", "SystemControlTransientBorderBrush" },
            { "MenuFlyoutItemBackground", "SystemControlTransparentBrush" },
            { "MenuFlyoutItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "MenuFlyoutItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "MenuFlyoutItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "MenuFlyoutItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "MenuFlyoutItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "MenuFlyoutSubItemBackground", "SystemControlTransparentBrush" },
            { "MenuFlyoutSubItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "MenuFlyoutSubItemBackgroundPressed", "SystemControlHighlightListAccentHighBrush" },
            { "MenuFlyoutSubItemBackgroundSubMenuOpened", "SystemControlHighlightListAccentLowBrush" },
            { "MenuFlyoutSubItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "MenuFlyoutSubItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "MenuFlyoutSubItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemForegroundSubMenuOpened", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "MenuFlyoutSubItemChevron", "SystemControlForegroundBaseMediumHighBrush" },
            { "MenuFlyoutSubItemChevronPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemChevronPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemChevronSubMenuOpened", "SystemControlHighlightAltBaseHighBrush" },
            { "MenuFlyoutSubItemChevronDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "MenuFlyoutLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "MenuFlyoutItemFocusedBackgroundThemeBrush", Color.FromArgb(0xFF, 0xE5, 0xE5, 0xE5) },
            { "MenuFlyoutItemFocusedForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "MenuFlyoutItemDisabledForegroundThemeBrush", Color.FromArgb(0x66, 0x00, 0x00, 0x00) },
            { "MenuFlyoutItemPointerOverBackgroundThemeBrush", Color.FromArgb(0xFF, 0xE5, 0xE5, 0xE5) },
            { "MenuFlyoutItemPointerOverForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "MenuFlyoutItemPressedBackgroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "MenuFlyoutItemPressedForegroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "MenuFlyoutSeparatorThemeBrush", Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A) },
            { "RatingControlUnselectedForeground", "SystemControlBackgroundBaseLowBrush" },
            { "RatingControlSelectedForeground", "SystemControlForegroundAccentBrush" },
            { "RatingControlPlaceholderForeground", "SystemControlForegroundBaseHighBrush" },
            { "RatingControlPointerOverPlaceholderForeground", "SystemControlForegroundBaseMediumBrush" },
            { "RatingControlPointerOverUnselectedForeground", "SystemControlForegroundBaseMediumBrush" },
            { "RatingControlPointerOverSelectedForeground", "SystemControlForegroundAccentBrush" },
            { "RatingControlDisabledSelectedForeground", "SystemBaseMediumLowColor" },
            { "RatingControlCaptionForeground", "SystemControlForegroundBaseMediumBrush" },
            { "NavigationViewDefaultPaneBackground", "SystemControlChromeMediumLowAcrylicElementMediumBrush" },
            { "SystemChromeMediumHighColor", Color.FromArgb(0xFF, 0xE6, 0xE6, 0xE6) },
            { "NavigationViewItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "NavigationViewItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "NavigationViewItemForegroundChecked", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundCheckedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundCheckedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "NavigationViewItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundSelectedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundSelectedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "NavigationViewItemForegroundSelectedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "NavigationViewItemBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "NavigationViewItemBorderBrushCheckedDisabled", "SystemControlTransparentBrush" },
            { "NavigationViewItemBorderBrushSelectedDisabled", "SystemControlTransparentBrush" },
            { "NavigationViewSelectionIndicatorForeground", "SystemControlForegroundAccentBrush" },
            { "TopNavigationViewItemForeground", "SystemControlForegroundBaseMediumBrush" },
            { "TopNavigationViewItemForegroundPointerOver", "SystemControlForegroundBaseMediumHighBrush" },
            { "TopNavigationViewItemForegroundPressed", "SystemControlForegroundBaseMediumHighBrush" },
            { "TopNavigationViewItemForegroundSelected", "SystemControlForegroundBaseHighBrush" },
            { "TopNavigationViewItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TopNavigationViewItemBackgroundPointerOver", "SystemControlTransparentBrush" },
            { "TopNavigationViewItemBackgroundPressed", "SystemControlTransparentBrush" },
            { "TopNavigationViewItemBackgroundSelected", "SystemControlTransparentBrush" },
            { "NavigationViewBackButtonBackground", "SystemControlTransparentBrush" },
            { "ColorPickerSliderThumbBackground", "SystemControlForegroundBaseHighBrush" },
            { "ColorPickerSliderThumbBackgroundPointerOver", "SystemControlHighlightChromeAltLowBrush" },
            { "ColorPickerSliderThumbBackgroundPressed", "SystemControlForegroundChromeHighBrush" },
            { "ColorPickerSliderThumbBackgroundDisabled", "SystemControlDisabledChromeDisabledHighBrush" },
            { "ColorPickerSliderTrackFillDisabled", "SystemControlDisabledBaseLowBrush" },
            { "PersonPictureForegroundThemeBrush", "SystemAltHighColor" },
            { "PersonPictureEllipseBadgeForegroundThemeBrush", "SystemBaseHighColor" },
            { "PersonPictureEllipseBadgeFillThemeBrush", "SystemChromeDisabledHighColor" },
            { "PersonPictureEllipseBadgeStrokeThemeBrush", "SystemListMediumColor" },
            { "PersonPictureEllipseFillThemeBrush", "SystemBaseMediumColor" },
            { "RefreshContainerForegroundBrush", "Black" },
            { "RefreshContainerBackgroundBrush", "Transparent" },
            { "RefreshVisualizerForeground", "Black" },
            { "RefreshVisualizerBackground", "Transparent" },
            { "MenuBarBackground", "SystemControlTransparentBrush" },
            { "MenuBarItemBackground", "SystemControlTransparentBrush" },
            { "MenuBarItemBackgroundPointerOver", "SystemControlBackgroundListLowBrush" },
            { "MenuBarItemBackgroundPressed", "SystemControlBackgroundListMediumBrush" },
            { "MenuBarItemBackgroundSelected", "SystemControlBackgroundListMediumBrush" },
            { "MenuBarItemBorderBrush", "SystemControlForegroundBaseMediumLowBrush" },
            { "MenuBarItemBorderBrushPointerOver", "SystemControlHighlightBaseMediumBrush" },
            { "MenuBarItemBorderBrushPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "MenuBarItemBorderBrushSelected", "SystemControlHighlightBaseMediumLowBrush" },
            { "AppBarButtonBackground", "SystemControlTransparentBrush" },
            { "AppBarButtonBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "AppBarButtonBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "AppBarButtonBackgroundDisabled", "SystemControlTransparentBrush" },
            { "AppBarButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "AppBarButtonForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarButtonBorderBrush", "SystemControlTransparentBrush" },
            { "AppBarButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "AppBarButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "AppBarButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBackground", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBackgroundDisabled", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBackgroundChecked", "SystemControlHighlightAccentBrush" },
            { "AppBarToggleButtonBackgroundCheckedPointerOver", "SystemControlHighlightAccentBrush" },
            { "AppBarToggleButtonBackgroundCheckedPressed", "SystemControlHighlightAccentBrush" },
            { "AppBarToggleButtonBackgroundCheckedDisabled", "SystemControlDisabledAccentBrush" },
            { "AppBarToggleButtonBackgroundHighLightOverlay", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBackgroundHighLightOverlayPointerOver", "SystemControlHighlightListLowBrush" },
            { "AppBarToggleButtonBackgroundHighLightOverlayPressed", "SystemControlHighlightListMediumBrush" },
            { "AppBarToggleButtonBackgroundHighLightOverlayCheckedPointerOver", "SystemControlHighlightListLowBrush" },
            { "AppBarToggleButtonBackgroundHighLightOverlayCheckedPressed", "SystemControlHighlightListMediumBrush" },
            { "AppBarToggleButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "AppBarToggleButtonForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonForegroundChecked", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonForegroundCheckedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonForegroundCheckedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonForegroundCheckedDisabled", "SystemControlBackgroundBaseMediumLowBrush" },
            { "AppBarToggleButtonBorderBrush", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushPointerOver", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushPressed", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushChecked", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushCheckedPointerOver", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushCheckedPressed", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonBorderBrushCheckedDisabled", "SystemControlTransparentBrush" },
            { "AppBarToggleButtonCheckGlyphForeground", "SystemControlForegroundBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundChecked", "SystemControlForegroundBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundCheckedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundCheckedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonCheckGlyphForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundCheckedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundCheckedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarToggleButtonOverflowLabelForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonCheckedBackgroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "AppBarToggleButtonCheckedBorderThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "AppBarToggleButtonCheckedDisabledBackgroundThemeBrush", Color.FromArgb(0x66, 0x00, 0x00, 0x00) },
            { "AppBarToggleButtonCheckedDisabledBorderThemeBrush", "Transparent" },
            { "AppBarToggleButtonCheckedDisabledForegroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "AppBarToggleButtonCheckedPointerOverBackgroundThemeBrush", Color.FromArgb(0x99, 0x00, 0x00, 0x00) },
            { "AppBarToggleButtonCheckedPointerOverBorderThemeBrush", Color.FromArgb(0x99, 0x00, 0x00, 0x00) },
            { "AppBarToggleButtonCheckedPressedBackgroundThemeBrush", "Transparent" },
            { "AppBarToggleButtonCheckedPressedBorderThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "AppBarToggleButtonCheckedPressedForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "AppBarToggleButtonCheckedForegroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "AppBarToggleButtonPointerOverBackgroundThemeBrush", Color.FromArgb(0x3D, 0x00, 0x00, 0x00) },
            { "ListBoxBackgroundThemeBrush", Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) },
            { "ListBoxBorderThemeBrush", Color.FromArgb(0x45, 0x00, 0x00, 0x00) },
            { "ListBoxDisabledForegroundThemeBrush", Color.FromArgb(0x66, 0x00, 0x00, 0x00) },
            { "ListBoxFocusBackgroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ListBoxForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "ListBoxItemDisabledForegroundThemeBrush", Color.FromArgb(0x66, 0x00, 0x00, 0x00) },
            { "ListBoxItemPointerOverBackgroundThemeBrush", Color.FromArgb(0x21, 0x00, 0x00, 0x00) },
            { "ListBoxItemPointerOverForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "ListBoxItemPressedBackgroundThemeBrush", Color.FromArgb(0xFF, 0xD3, 0xD3, 0xD3) },
            { "ListBoxItemPressedForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "ListBoxItemSelectedBackgroundThemeBrush", Color.FromArgb(0xFF, 0x46, 0x17, 0xB4) },
            { "ListBoxItemSelectedDisabledBackgroundThemeBrush", Color.FromArgb(0x8C, 0x00, 0x00, 0x00) },
            { "ListBoxItemSelectedDisabledForegroundThemeBrush", Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) },
            { "ListBoxItemSelectedForegroundThemeBrush", "White" },
            { "ListBoxItemSelectedPointerOverBackgroundThemeBrush", Color.FromArgb(0xFF, 0x5F, 0x37, 0xBE) },
            { "CommandBarBackground", "SystemControlBackgroundChromeMediumBrush" },
            { "CommandBarForeground", "SystemControlForegroundBaseHighBrush" },
            { "CommandBarHighContrastBorder", "SystemControlForegroundTransparentBrush" },
            { "CommandBarEllipsisIconForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "CommandBarOverflowPresenterBackground", "SystemControlTransientBackgroundBrush" },
            { "CommandBarOverflowPresenterBorderBrush", "SystemControlTransientBorderBrush" },
            { "CommandBarLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "ListViewItemBackground", "SystemControlTransparentBrush" },
            { "ListViewItemBackgroundPointerOver", "SystemControlHighlightListLowBrush" },
            { "ListViewItemBackgroundPressed", "SystemControlHighlightListMediumBrush" },
            { "ListViewItemBackgroundSelected", "SystemControlHighlightListAccentLowBrush" },
            { "ListViewItemBackgroundSelectedPointerOver", "SystemControlHighlightListAccentMediumBrush" },
            { "ListViewItemBackgroundSelectedPressed", "SystemControlHighlightListAccentHighBrush" },
            { "ListViewItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "ListViewItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "ListViewItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "ListViewItemFocusVisualPrimaryBrush", "SystemControlFocusVisualPrimaryBrush" },
            { "ListViewItemFocusVisualSecondaryBrush", "SystemControlFocusVisualSecondaryBrush" },
            { "ListViewItemFocusBorderBrush", "SystemControlForegroundAltHighBrush" },
            { "ListViewItemFocusSecondaryBorderBrush", "SystemControlForegroundBaseHighBrush" },
            { "ListViewItemCheckBrush", "SystemControlForegroundBaseMediumHighBrush" },
            { "ListViewItemCheckBoxBrush", "SystemControlForegroundBaseMediumHighBrush" },
            { "ListViewItemDragBackground", "SystemControlTransparentBrush" },
            { "ListViewItemDragForeground", "SystemControlHighlightAltChromeWhiteBrush" },
            { "ListViewItemPlaceholderBackground", "SystemControlDisabledChromeDisabledHighBrush" },
            { "ListViewItemCheckHintThemeBrush", Color.FromArgb(0xFF, 0x46, 0x17, 0xB4) },
            { "ListViewItemCheckSelectingThemeBrush", Color.FromArgb(0xFF, 0x46, 0x17, 0xB4) },
            { "ListViewItemCheckThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ListViewItemDragBackgroundThemeBrush", Color.FromArgb(0x99, 0x46, 0x17, 0xB4) },
            { "ListViewItemDragForegroundThemeBrush", "White" },
            { "ListViewItemFocusBorderThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "ListViewItemOverlayBackgroundThemeBrush", Color.FromArgb(0xA6, 0x00, 0x00, 0x00) },
            { "ListViewItemOverlaySecondaryForegroundThemeBrush", Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) },
            { "ListViewItemOverlayForegroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "ListViewItemPlaceholderBackgroundThemeBrush", Color.FromArgb(0xFF, 0x3D, 0x3D, 0x3D) },
            { "ListViewItemPointerOverBackgroundThemeBrush", Color.FromArgb(0x4D, 0x00, 0x00, 0x00) },
            { "ListViewItemSelectedBackgroundThemeBrush", Color.FromArgb(0xFF, 0x46, 0x17, 0xB4) },
            { "ListViewItemSelectedForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "ListViewItemSelectedPointerOverBackgroundThemeBrush", Color.FromArgb(0xFF, 0x5F, 0x37, 0xBE) },
            { "ListViewItemSelectedPointerOverBorderThemeBrush", Color.FromArgb(0xFF, 0x5F, 0x37, 0xBE) },
            { "TextBoxForegroundHeaderThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "TextBoxPlaceholderTextThemeBrush", Color.FromArgb(0xAB, 0x00, 0x00, 0x00) },
            { "TextBoxBackgroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "TextBoxBorderThemeBrush", Color.FromArgb(0xA3, 0x00, 0x00, 0x00) },
            { "TextBoxButtonBackgroundThemeBrush", "Transparent" },
            { "TextBoxButtonBorderThemeBrush", "Transparent" },
            { "TextBoxButtonForegroundThemeBrush", Color.FromArgb(0x99, 0x00, 0x00, 0x00) },
            { "TextBoxButtonPointerOverBackgroundThemeBrush", Color.FromArgb(0xFF, 0xDE, 0xDE, 0xDE) },
            { "TextBoxButtonPointerOverBorderThemeBrush", "Transparent" },
            { "TextBoxButtonPointerOverForegroundThemeBrush", "Black" },
            { "TextBoxButtonPressedBackgroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "TextBoxButtonPressedBorderThemeBrush", "Transparent" },
            { "TextBoxButtonPressedForegroundThemeBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
            { "TextBoxDisabledBackgroundThemeBrush", Color.FromArgb(0x66, 0xCA, 0xCA, 0xCA) },
            { "TextBoxDisabledBorderThemeBrush", Color.FromArgb(0x26, 0x00, 0x00, 0x00) },
            { "TextBoxDisabledForegroundThemeBrush", Color.FromArgb(0xFF, 0x66, 0x66, 0x66) },
            { "TextBoxForegroundThemeBrush", Color.FromArgb(0xFF, 0x00, 0x00, 0x00) },
            { "AutoSuggestBoxSuggestionsListBackground", "SystemControlTransientBackgroundBrush" },
            { "AutoSuggestBoxSuggestionsListBorderBrush", "SystemControlTransientBorderBrush" },
            { "AutoSuggestBoxLightDismissOverlayBackground", "SystemControlPageBackgroundMediumAltMediumBrush" },
            { "TreeViewItemBackgroundDisabled", "SystemControlTransparentBrush" },
            { "TreeViewItemBackgroundSelectedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TreeViewItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "TreeViewItemForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "TreeViewItemForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "TreeViewItemForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TreeViewItemForegroundSelected", "SystemControlHighlightAltBaseHighBrush" },
            { "TreeViewItemForegroundSelectedPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "TreeViewItemForegroundSelectedPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "TreeViewItemForegroundSelectedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "TreeViewItemBorderBrush", "SystemControlTransparentBrush" },
            { "TreeViewItemBorderBrushDisabled", "SystemControlTransparentBrush" },
            { "TreeViewItemBorderBrushSelected", "SystemControlTransparentBrush" },
            { "TreeViewItemBorderBrushSelectedDisabled", "SystemControlTransparentBrush" },
            { "TreeViewItemCheckBoxBackgroundSelected", "SystemControlTransparentBrush" },
            { "TreeViewItemCheckBoxBorderSelected", "SystemControlForegroundBaseMediumHighBrush" },
            { "TreeViewItemCheckGlyphSelected", "SystemControlForegroundBaseMediumHighBrush" },
            { "SwipeItemBackground", "SystemControlBackgroundBaseLowBrush" },
            { "SwipeItemForeground", "SystemControlForegroundBaseHighBrush" },
            { "SwipeItemBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "SwipeItemPreThresholdExecuteForeground", "SystemControlBackgroundBaseMediumBrush" },
            { "SwipeItemPreThresholdExecuteBackground", "SystemControlBackgroundBaseLowBrush" },
            { "SwipeItemPostThresholdExecuteForeground", "SystemControlForegroundChromeWhiteBrush" },
            { "SwipeItemPostThresholdExecuteBackground", "SystemControlBackgroundAccentBrush" },
            { "SplitButtonBackground", "SystemControlBackgroundBaseLowBrush" },
            { "SplitButtonBackgroundPointerOver", "SystemControlBackgroundBaseLowBrush" },
            { "SplitButtonBackgroundPressed", "SystemControlBackgroundBaseMediumLowBrush" },
            { "SplitButtonBackgroundDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "SplitButtonBackgroundChecked", "SystemControlHighlightAccentBrush" },
            { "SplitButtonBackgroundCheckedPointerOver", "SystemControlHighlightAccentBrush" },
            { "SplitButtonBackgroundCheckedPressed", "SystemControlHighlightBaseMediumLowBrush" },
            { "SplitButtonBackgroundCheckedDisabled", "SystemControlBackgroundBaseLowBrush" },
            { "SplitButtonForeground", "SystemControlForegroundBaseHighBrush" },
            { "SplitButtonForegroundPointerOver", "SystemControlHighlightBaseHighBrush" },
            { "SplitButtonForegroundPressed", "SystemControlHighlightBaseHighBrush" },
            { "SplitButtonForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "SplitButtonForegroundChecked", "SystemControlHighlightAltChromeWhiteBrush" },
            { "SplitButtonForegroundCheckedPointerOver", "SystemControlHighlightAltChromeWhiteBrush" },
            { "SplitButtonForegroundCheckedPressed", "SystemControlHighlightAltChromeWhiteBrush" },
            { "SplitButtonForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "SplitButtonBorderBrush", "SystemControlForegroundTransparentBrush" },
            { "SplitButtonBorderBrushPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "SplitButtonBorderBrushPressed", "SystemControlHighlightTransparentBrush" },
            { "SplitButtonBorderBrushDisabled", "SystemControlDisabledTransparentBrush" },
            { "SplitButtonBorderBrushChecked", "SystemControlHighlightAltTransparentBrush" },
            { "SplitButtonBorderBrushCheckedPointerOver", "SystemControlHighlightBaseMediumLowBrush" },
            { "SplitButtonBorderBrushCheckedPressed", "SystemControlHighlightAltTransparentBrush" },
            { "SplitButtonBorderBrushCheckedDisabled", "SystemControlDisabledTransparentBrush" },
            { "CommandBarFlyoutButtonBackground", "SystemControlTransparentBrush" },
            { "MenuFlyoutItemKeyboardAcceleratorTextForeground", "SystemControlForegroundBaseMediumBrush" },
            { "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "SystemControlHighlightAltBaseMediumBrush" },
            { "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarButtonKeyboardAcceleratorTextForeground", "SystemControlForegroundBaseMediumBrush" },
            { "AppBarButtonKeyboardAcceleratorTextForegroundPointerOver", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarButtonKeyboardAcceleratorTextForegroundPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarButtonKeyboardAcceleratorTextForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForeground", "SystemControlForegroundBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundPointerOver", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundChecked", "SystemControlForegroundBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedPointerOver", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedPressed", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarToggleButtonKeyboardAcceleratorTextForegroundCheckedDisabled", "SystemControlDisabledBaseMediumLowBrush" },
            { "AppBarButtonBackgroundSubMenuOpened", "SystemControlHighlightListAccentLowBrush" },
            { "AppBarButtonForegroundSubMenuOpened", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonKeyboardAcceleratorTextForegroundSubMenuOpened", "SystemControlHighlightAltBaseMediumBrush" },
            { "AppBarButtonBorderBrushSubMenuOpened", "SystemControlTransparentBrush" },
            { "AppBarButtonSubItemChevronForeground", "SystemControlForegroundBaseMediumHighBrush" },
            { "AppBarButtonSubItemChevronForegroundPointerOver", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonSubItemChevronForegroundPressed", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonSubItemChevronForegroundSubMenuOpened", "SystemControlHighlightAltBaseHighBrush" },
            { "AppBarButtonSubItemChevronForegroundDisabled", "SystemControlDisabledBaseMediumLowBrush" },
        };
    }

    public class ThemeCustomInfo : ThemeInfoBase
    {
        public ThemeCustomInfo(bool official = false)
        {
            Values = new Dictionary<string, object>();
            IsOfficial = official;
        }

        public Dictionary<string, object> Values { get; private set; }

        public string Path { get; set; }

        public override bool IsOfficial { get; }

        public static bool Equals(ThemeCustomInfo x, ThemeCustomInfo y)
        {
            if (x.Parent != y.Parent)
            {
                return false;
            }

            bool equal = false;
            if (x.Values.Count == y.Values.Count) // Require equal count.
            {
                equal = true;
                foreach (var pair in x.Values)
                {
                    if (y.Values.TryGetValue(pair.Key, out object value))
                    {
                        // Require value be equal.
                        if (!Equals(value, pair.Value))
                        {
                            equal = false;
                            break;
                        }
                    }
                    else
                    {
                        // Require key be present.
                        equal = false;
                        break;
                    }
                }
            }

            return equal;
        }



        public override Color ChatBackgroundColor
        {
            get
            {
                //if (Values.TryGet("PageHeaderBackgroundBrush", out Color color))
                //{
                //    return color;
                //}

                if (Values.TryGet("ApplicationPageBackgroundThemeBrush", out Color color))
                {
                    return color;
                }

                return base.ChatBackgroundColor;
            }
        }

        public override Color ChatBorderColor
        {
            get
            {
                //if (Values.TryGet("PageHeaderBackgroundBrush", out Color color))
                //{
                //    return color;
                //}

                if (Values.TryGet("PageHeaderBackgroundBrush", out Color color))
                {
                    return color;
                }

                return base.ChatBorderColor;
            }
        }

        public override Color MessageBackgroundColor
        {
            get
            {
                if (Values.TryGet("MessageBackgroundColor", out Color color))
                {
                    return color;
                }

                return base.MessageBackgroundColor;
            }
        }

        public override Color MessageBackgroundOutColor
        {
            get
            {
                if (Values.TryGet("MessageBackgroundOutColor", out Color color))
                {
                    return color;
                }

                return base.MessageBackgroundOutColor;
            }
        }
    }

    public class ThemeBundledInfo : ThemeInfoBase
    {
        public override bool IsOfficial => true;
    }

    public class ThemeSystemInfo : ThemeInfoBase
    {
        public override bool IsOfficial => true;

        public override Color ChatBackgroundColor => ((App)App.Current).UISettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);

        public override Color ChatBorderColor
        {
            get
            {
                if (SettingsService.Current.Appearance.GetSystemTheme() == TelegramAppTheme.Light)
                {
                    return Color.FromArgb(0xFF, 0xe6, 0xe6, 0xe6);
                }

                return Color.FromArgb(0xFF, 0x2b, 0x2b, 0x2b);
            }
        }

        public override Color MessageBackgroundColor
        {
            get
            {
                if (SettingsService.Current.Appearance.GetSystemTheme() == TelegramAppTheme.Light)
                {
                    return Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
                }

                return Color.FromArgb(0xFF, 0x1F, 0x2C, 0x36);
            }
        }

        public override Color MessageBackgroundOutColor
        {
            get
            {
                if (SettingsService.Current.Appearance.GetSystemTheme() == TelegramAppTheme.Light)
                {
                    return ((App)App.Current).UISettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight3);
                }

                return ((App)App.Current).UISettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentDark2);
            }
        }
    }

    public abstract class ThemeInfoBase
    {
        public string Name { get; set; }
        public TelegramTheme Parent { get; set; }

        public abstract bool IsOfficial { get; }



        public virtual Color ChatBackgroundColor
        {
            get
            {
                if (Parent.HasFlag(TelegramTheme.Light))
                {
                    return Color.FromArgb(0xFF, 0xdf, 0xe4, 0xe8);
                }

                return Color.FromArgb(0xFF, 0x10, 0x14, 0x16);
            }
        }

        public virtual Color ChatBorderColor
        {
            get
            {
                if (Parent.HasFlag(TelegramTheme.Light))
                {
                    return Color.FromArgb(0xFF, 0xe6, 0xe6, 0xe6);
                }

                return Color.FromArgb(0xFF, 0x2b, 0x2b, 0x2b);
            }
        }

        public virtual Color MessageBackgroundColor
        {
            get
            {
                if (Parent.HasFlag(TelegramTheme.Light))
                {
                    return Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
                }

                return Color.FromArgb(0xFF, 0x18, 0x25, 0x33);
            }
        }

        public virtual Color MessageBackgroundOutColor
        {
            get
            {
                if (Parent.HasFlag(TelegramTheme.Light))
                {
                    return Color.FromArgb(0xFF, 0xF0, 0xFD, 0xDF);
                }

                return Color.FromArgb(0xFF, 0x2B, 0x52, 0x78);
            }
        }
    }
}
