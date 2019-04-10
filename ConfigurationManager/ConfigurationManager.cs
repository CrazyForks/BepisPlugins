﻿// Made by MarC0 / ManlyMarco
// Copyright 2018 GNU General Public License v3.0

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using BepisPlugins;
using BepInEx;
using ConfigurationManager.Utilities;
using BepInEx.Logging;
using UnityEngine;
using Logger = BepInEx.Logger;

namespace ConfigurationManager
{
    [BepInPlugin(GUID, "Configuration Manager", Version)]
    [Browsable(false)]
    public class ConfigurationManager : BaseUnityPlugin
    {
        public const string GUID = "com.bepis.bepinex.configurationmanager";
        public const string Version = Metadata.PluginsVersion;

        private static readonly GUIContent KeyboardShortcutsCategoryName = new GUIContent("Keyboard shortcuts",
            "The first key is the main key, while the rest are modifiers." +
            "\nThe shortcut will only fire when you press \n" +
            "the main key while all modifiers are already pressed.");

        private static readonly ICollection<string> UpdateMethodNames = new[]
        {
            "Update",
            "FixedUpdate",
            "LateUpdate",
            "OnGUI"
        };

        public event EventHandler<ValueChangedEventArgs<bool>> DisplayingWindowChanged;
        public Action KeyPressedOverride;

        private Dictionary<Type, Action<PropSettingEntry>> _settingDrawHandlers;

        private bool _displayingWindow;

        private readonly SettingFieldDrawer _fieldDrawer = new SettingFieldDrawer();
        private string _modsWithoutSettings;

        private List<PropSettingEntry> _settings;

        private Rect _settingWindowRect, _screenRect;
        private Vector2 _settingWindowScrollPos;

        public static Texture2D TooltipBg;
        public static Texture2D WindowBackground;

        private readonly ConfigWrapper<bool> _showAdvanced;
        private readonly ConfigWrapper<bool> _showKeybinds;
        private readonly ConfigWrapper<bool> _showSettings;
        private bool _showDebug;

        public ConfigurationManager()
        {
            _showAdvanced = new ConfigWrapper<bool>("showAdvanced", this, false);
            _showKeybinds = new ConfigWrapper<bool>("showKeybinds", this, true);
            _showSettings = new ConfigWrapper<bool>("showSettings", this, true);
        }

        public bool DisplayingWindow
        {
            get => _displayingWindow;
            set
            {
                if (_displayingWindow == value) return;
                _displayingWindow = value;

                if (_displayingWindow)
                {
                    CalculateWindowRect();

                    BuildSettingList();
                }

                DisplayingWindowChanged?.Invoke(this, new ValueChangedEventArgs<bool>(value));
            }
        }

        private void BuildSettingList()
        {
            CollectSettings(out var results, out var modsWithoutSettings, _showDebug);

            if (!_showAdvanced.Value)
                results = results.Where(x => x.IsAdvanced != true);
            if (!_showKeybinds.Value)
                results = results.Where(x => x.SettingType != typeof(KeyboardShortcut));
            if (!_showSettings.Value)
                results = results.Where(x => x.IsAdvanced == true || x.SettingType == typeof(KeyboardShortcut));

            _settings = results.ToList();

            _modsWithoutSettings = string.Join(", ", modsWithoutSettings.Select(x => x.TrimStart('!')).OrderBy(x => x).ToArray());
        }

        private static void CollectSettings(out IEnumerable<PropSettingEntry> results, out List<string> modsWithoutSettings, bool showDebug)
        {
            var baseSettingType = typeof(ConfigWrapper<>);
            results = Enumerable.Empty<PropSettingEntry>();
            modsWithoutSettings = new List<string>();
            foreach (var plugin in Utils.FindPlugins())
            {
                var type = plugin.GetType();

                var pluginInfo = MetadataHelper.GetMetadata(type);
                if (pluginInfo == null)
                {
                    Logger.Log(LogLevel.Error, $"Plugin {type.FullName} is missing the BepInPlugin attribute!");
                    modsWithoutSettings.Add(type.FullName);
                    continue;
                }

                if (type.GetCustomAttributes(typeof(BrowsableAttribute), false).Cast<BrowsableAttribute>()
                    .Any(x => !x.Browsable))
                {
                    modsWithoutSettings.Add(pluginInfo.Name);
                    continue;
                }

                var detected = new List<PropSettingEntry>();

                // Config wrappers ------

                var settingProps = type
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FilterBrowsable(true, true);

                var settingFields = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .Where(f => !f.IsSpecialName)
                    .FilterBrowsable(true, true)
                    .Select(f => new FieldToPropertyInfoWrapper(f));

                var settingEntries = settingProps.Concat(settingFields.Cast<PropertyInfo>())
                    .Where(x => x.PropertyType.IsSubclassOfRawGeneric(baseSettingType));

                detected.AddRange(settingEntries.Select(x => PropSettingEntry.FromConfigWrapper(plugin, x, pluginInfo, plugin)).Where(x => x != null));

                // Config wrappers static ------

                var settingStaticProps = type
                    .GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FilterBrowsable(true, true);

                var settingStaticFields = type.GetFields(BindingFlags.Static | BindingFlags.Public)
                    .Where(f => !f.IsSpecialName)
                    .FilterBrowsable(true, true)
                    .Select(f => new FieldToPropertyInfoWrapper(f));

                var settingStaticEntries = settingStaticProps.Concat(settingStaticFields.Cast<PropertyInfo>())
                    .Where(x => x.PropertyType.IsSubclassOfRawGeneric(baseSettingType));

                detected.AddRange(settingStaticEntries.Select(x => PropSettingEntry.FromConfigWrapper(null, x, pluginInfo, plugin)).Where(x => x != null));

                // Normal properties ------

                bool IsPropSafeToShow(PropertyInfo p)
                {
                    return p.GetSetMethod()?.IsPublic == true && (p.PropertyType.IsValueType || p.PropertyType == typeof(string));
                }

                var normalPropsSafeToShow = type
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(IsPropSafeToShow)
                    .FilterBrowsable(true, true)
                    .Where(x => !x.PropertyType.IsSubclassOfRawGeneric(baseSettingType));

                var normalPropsWithBrowsable = type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FilterBrowsable(true, false)
                    .Where(x => !x.PropertyType.IsSubclassOfRawGeneric(baseSettingType));

                var normalProps = normalPropsSafeToShow.Concat(normalPropsWithBrowsable).Distinct();

                detected.AddRange(normalProps.Select(x => PropSettingEntry.FromNormalProperty(plugin, x, pluginInfo, plugin)).Where(x => x != null));

                // Normal static properties ------

                var normalStaticPropsSafeToShow = type
                    .GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(IsPropSafeToShow)
                    .FilterBrowsable(true, true)
                    .Where(x => !x.PropertyType.IsSubclassOfRawGeneric(baseSettingType));

                var normalStaticPropsWithBrowsable = type.GetProperties(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .FilterBrowsable(true, false)
                    .Where(x => !x.PropertyType.IsSubclassOfRawGeneric(baseSettingType));

                var normalStaticProps = normalStaticPropsSafeToShow.Concat(normalStaticPropsWithBrowsable).Distinct();

                detected.AddRange(normalStaticProps.Select(x => PropSettingEntry.FromNormalProperty(null, x, pluginInfo, plugin)).Where(x => x != null));

                detected.RemoveAll(x => x.Browsable == false);

                if(!detected.Any())
                    modsWithoutSettings.Add(pluginInfo.Name);

                // Allow to enable/disable plugin if it uses any update methods ------
                if (showDebug && type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(x => UpdateMethodNames.Contains(x.Name)))
                {
                    var enabledSetting =
                        PropSettingEntry.FromNormalProperty(plugin, type.GetProperty("enabled"), pluginInfo, plugin);
                    enabledSetting.DispName = "!Allow plugin to run on every frame";
                    enabledSetting.Description =
                        "Disabling this will disable some or all of the plugin's functionality.\nHooks and event-based functionality will not be disabled.\nThis setting will be lost after game restart.";
                    enabledSetting.IsAdvanced = true;
                    detected.Add(enabledSetting);
                }

                if (detected.Any())
                {
                    var isAdvancedPlugin = type.GetCustomAttributes(typeof(AdvancedAttribute), false).Cast<AdvancedAttribute>()
                        .Any(x => x.IsAdvanced);
                    if (isAdvancedPlugin)
                        detected.ForEach(entry => entry.IsAdvanced = true);

                    results = results.Concat(detected);
                }
            }
        }

        private void CalculateWindowRect()
        {
            var size = new Vector2(Mathf.Min(Screen.width - 100, 650), Screen.height - 100);
            var offset = new Vector2((Screen.width - size.x) / 2, (Screen.height - size.y) / 2);
            _settingWindowRect = new Rect(offset, size);

            _screenRect = new Rect(0, 0, Screen.width, Screen.height);
        }

        protected void OnGUI()
        {
            if (DisplayingWindow)
            {
                if (GUI.Button(_screenRect, string.Empty, GUI.skin.box) &&
                    !_settingWindowRect.Contains(Input.mousePosition))
                    DisplayingWindow = false;

                GUI.Box(_settingWindowRect, GUIContent.none, new GUIStyle { normal = new GUIStyleState { background = WindowBackground } });

                GUILayout.Window(-68, _settingWindowRect, SettingsWindow, "Plugin / mod settings");
            }
        }

        private static void DrawTooltip(Rect area)
        {
            if (!string.IsNullOrEmpty(GUI.tooltip))
            {
                var currentEvent = Event.current;

                var style = new GUIStyle
                {
                    normal = new GUIStyleState { textColor = Color.white, background = TooltipBg },
                    wordWrap = true,
                    alignment = TextAnchor.MiddleCenter
                };

                const int width = 400;
                var height = style.CalcHeight(new GUIContent(GUI.tooltip), 400) + 10;

                var x = currentEvent.mousePosition.x + width > area.width
                    ? area.width - width
                    : currentEvent.mousePosition.x;

                var y = currentEvent.mousePosition.y + 25 + height > area.height
                    ? currentEvent.mousePosition.y - height
                    : currentEvent.mousePosition.y + 25;

                GUI.Box(new Rect(x, y, width, height), GUI.tooltip, style);
            }
        }

        private void SettingsWindow(int id)
        {
            _settingWindowScrollPos = GUILayout.BeginScrollView(_settingWindowScrollPos);
            GUILayout.BeginVertical();
            {
                DrawWindowHeader();

                foreach (var plugin in _settings.GroupBy(x => x.PluginInfo).OrderBy(x => x.Key.Name))
                    DrawSinglePlugin(plugin);

                GUILayout.Space(10);
                GUILayout.Label("Plugins with no options available: " + _modsWithoutSettings);
            }
            GUILayout.EndVertical();
            GUILayout.EndScrollView();

            if (!_fieldDrawer.DrawCurrentDropdown())
                DrawTooltip(_settingWindowRect);
        }

        private void DrawWindowHeader()
        {
            GUILayout.BeginHorizontal(GUI.skin.box);
            {
                GUILayout.Label("Show:", GUILayout.ExpandWidth(false));
                var newVal = GUILayout.Toggle(_showSettings.Value, "Normal settings");
                if (_showSettings.Value != newVal)
                {
                    _showSettings.Value = newVal;
                    BuildSettingList();
                }

                newVal = GUILayout.Toggle(_showKeybinds.Value, "Keyboard shortcuts");
                if (_showKeybinds.Value != newVal)
                {
                    _showKeybinds.Value = newVal;
                    BuildSettingList();
                }

                newVal = GUILayout.Toggle(_showAdvanced.Value, "Advanced settings");
                if (_showAdvanced.Value != newVal)
                {
                    _showAdvanced.Value = newVal;
                    BuildSettingList();
                }

                newVal = GUILayout.Toggle(_showDebug, "Debug settings");
                if (_showDebug != newVal)
                {
                    _showDebug = newVal;
                    BuildSettingList();
                }
            }
            GUILayout.EndHorizontal();
        }

        private void DrawSinglePlugin(IGrouping<BepInPlugin, PropSettingEntry> plugin)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            {
                _fieldDrawer.DrawCenteredLabel($"{plugin.Key.Name.TrimStart('!')} {plugin.Key.Version}");

                foreach (var category in plugin.Select(x => new
                {
                    plugin = x,
                    category = x.SettingType == typeof(KeyboardShortcut)
                            ? KeyboardShortcutsCategoryName
                            : new GUIContent(x.Category)
                })
                    .GroupBy(a => a.category.text).OrderBy(x => x.Key))
                {
                    if (!string.IsNullOrEmpty(category.Key))
                        _fieldDrawer.DrawCenteredLabel(category.First().category);

                    foreach (var setting in category.OrderBy(x => x.plugin.DispName))
                    {
                        DrawSingleSetting(setting.plugin);
                        GUILayout.Space(2);
                    }
                }
            }
            GUILayout.EndVertical();
        }

        private void DrawSingleSetting(PropSettingEntry setting)
        {
            GUILayout.BeginHorizontal();
            {
                GUILayout.Label(new GUIContent(setting.DispName.TrimStart('!'), setting.Description),
                    GUILayout.Width(_settingWindowRect.width / 2.5f));

                if (setting.AcceptableValues is CustomSettingDrawAttribute customDrawer)
                    customDrawer.Run(setting.PluginInstance);
                else if (setting.AcceptableValues is AcceptableValueRangeAttribute range)
                    _fieldDrawer.DrawRangeField(setting, range);
                else if (setting.AcceptableValues is AcceptableValueListAttribute list)
                {
                    try
                    {
                        var acceptableValues = list.GetAcceptableValues(setting.PluginInstance);
                        if (acceptableValues == null || acceptableValues.Length == 0)
                            throw new ArgumentException("AcceptableValueListAttribute returned a null or empty list of acceptable values. You need to supply at least 1 option.");

                        if (!setting.SettingType.IsInstanceOfType(acceptableValues.FirstOrDefault(x => x != null)))
                            throw new ArgumentException("AcceptableValueListAttribute returned a list with items of type other than the settng type itself.");

                        _fieldDrawer.DrawComboboxField(setting, acceptableValues, _settingWindowRect.yMax);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(LogLevel.Error, "[ConfigManager] Failed to get acceptable values - " + ex);
                        GUILayout.Label("Failed to get dropdown values");
                    }
                }
                else if (setting.SettingType.IsEnum)
                    _fieldDrawer.DrawComboboxField(setting, Enum.GetValues(setting.SettingType), _settingWindowRect.yMax);
                else
                    DrawFieldBasedOnValueType(setting);

                if (setting.DefaultValue != null)
                {
                    if (DrawDefaultButton())
                        setting.Set(setting.DefaultValue);
                }
                else if (setting.Wrapper != null)
                {
                    var method = setting.Wrapper.GetType()
                        .GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public);
                    if (method != null && DrawDefaultButton())
                        method.Invoke(setting.Wrapper, null);
                }
            }
            GUILayout.EndHorizontal();
        }
        private void DrawFieldBasedOnValueType(PropSettingEntry setting)
        {
            if (_settingDrawHandlers.TryGetValue(setting.SettingType, out var drawMethod))
                drawMethod(setting);
            else
                _fieldDrawer.DrawUnknownField(setting);
        }

        private static bool DrawDefaultButton()
        {
            GUILayout.Space(5);
            return GUILayout.Button("Reset", GUILayout.ExpandWidth(false));
        }

        protected void Start()
        {
            _settingDrawHandlers = new Dictionary<Type, Action<PropSettingEntry>>
            {
                {typeof(bool), _fieldDrawer.DrawBoolField},
                {typeof(KeyboardShortcut), _fieldDrawer.DrawKeyboardShortcut}
            };

            var background = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            background.SetPixel(0, 0, Color.black);
            background.Apply();
            TooltipBg = background;

            var windowBackground = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            windowBackground.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1));
            windowBackground.Apply();
            WindowBackground = windowBackground;
        }

        protected void Update()
        {
            if (Input.GetKeyUp(KeyCode.F1))
            {
                if (KeyPressedOverride != null)
                    KeyPressedOverride();
                else
                    DisplayingWindow = !DisplayingWindow;
            }
        }
    }
}
