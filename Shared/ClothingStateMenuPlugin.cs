﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using ChaCustom;
using HarmonyLib;
using KKAPI.Maker;
using KKAPI.Maker.UI.Sidebar;
using UniRx;
using UnityEngine;

namespace ClothingStateMenu
{
    public partial class ClothingStateMenuPlugin : BaseUnityPlugin
    {
        public const string Version = "3.0.1";
        public const string GUID = "ClothingStateMenu";

        private const float Height = 20f;
        private const float Margin = 5f;
        private const float Width = 117f;

        private readonly List<IStateToggleButton> _buttons = new List<IStateToggleButton>();

        private Vector2 _accessorySlotsScrollPos = Vector2.zero;
        private Rect _accesorySlotsRect;

        private ChaControl _chaCtrl;
        private SidebarToggle _sidebarToggle;

        private bool _showOutsideMaker;
        private ConfigEntry<bool> ShowInMaker { get; set; }

#if KK
        private ConfigEntry<bool> ShowCoordinateButtons { get; set; }
        private Action<int> _setCoordAction;
#endif

        private Func<ChaFile, object> _getMoreaccsData;
        private object _moreAccsObj;

        private ConfigEntry<KeyboardShortcut> Keybind { get; set; }

        private void Start()
        {
            ShowInMaker = Config.Bind("General", "Show in Character Maker", false, "Show the clothing state menu in character maker. Can be enabled from maker interface or by pressing the keyboard shortcut.");
            ShowInMaker.SettingChanged += (sender, args) =>
            {
                if (MakerAPI.InsideMaker)
                    ShowInterface = ShowInMaker.Value;
            };

#if KK
            ShowCoordinateButtons = Config.Bind("General", "Show coordinate change buttons in Character Maker", false, "Adds buttons to the menu that allow quickly switching between clothing sets. Same as using the clothing dropdown.\nThe buttons are always shown outside of character maker.");
            ShowCoordinateButtons.SettingChanged += (sender, args) =>
            {
                if (ShowInterface)
                    ShowInterface = true;
            };
#endif

            Keybind = Config.Bind("General", "Toggle clothing state menu", new KeyboardShortcut(KeyCode.Tab, KeyCode.LeftShift), "Keyboard shortcut to toggle the clothing state menu on and off.\nCan be used outside of character maker in some cases - works for males in H scenes (the male has to be visible for the menu to appear) and in some conversations with girls.");

            MakerAPI.RegisterCustomSubCategories += (sender, e) =>
            {
                _sidebarToggle = e.AddSidebarControl(new SidebarToggle("Show clothing state menu", ShowInMaker.Value, this));
                _sidebarToggle.ValueChanged.Subscribe(b => ShowInterface = b);
            };
            MakerAPI.MakerExiting += (sender, e) =>
            {
                _chaCtrl = null;
#if KK
                _setCoordAction = null;
#endif
                _sidebarToggle = null;
            };

            var moreaccsType = Type.GetType("MoreAccessoriesKOI.MoreAccessories, MoreAccessories");
            if (moreaccsType != null)
            {
                try
                {
                    _moreAccsObj = Traverse.Create(moreaccsType).Field("_self").GetValue();
                    var dic = Traverse.Create(_moreAccsObj).Field("_accessoriesByChar").GetValue();
                    var tryMethod = AccessTools.Method(dic.GetType(), "TryGetValue");
                    _getMoreaccsData = control =>
                    {
                        var parameters = new object[] { control, null };
                        tryMethod.Invoke(dic, parameters);
                        return parameters[1];
                    };
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex);
                }
            }
        }

        private bool ShowInterface
        {
            get
            {
                if (MakerAPI.InsideMaker)
                {
                    if (!ShowInMaker.Value)
                        return false;
                }
                else
                {
                    if (!_showOutsideMaker)
                        return false;
                    if (_chaCtrl == null)
                    {
                        ShowInterface = false;
                        return false;
                    }
                }

                return CanShow();
            }
            set
            {
                if (MakerAPI.InsideMaker)
                {
                    ShowInMaker.Value = value;
                    if (_sidebarToggle != null) _sidebarToggle.Value = value;
                }
                else
                    _showOutsideMaker = value;

                _chaCtrl = null;
                _buttons.Clear();

                if (!value) return;

                FindTargetCharacter();

                if (_chaCtrl == null)
                {
                    _showOutsideMaker = false;
                    return;
                }

                SetupInterface();
            }
        }

        private bool CanShow()
        {
            if (_chaCtrl == null) return false;
            if (!_chaCtrl.visibleAll) return false;

            if (MakerAPI.InsideMaker && !MakerAPI.IsInterfaceVisible()) return false;

            if (Manager.Scene.Instance.AddSceneName == "Config") return false;
            if (Manager.Scene.Instance.AddSceneName != Manager.Scene.Instance.AddSceneNameOverlapRemoved) return false;
            if (Manager.Scene.Instance.IsNowLoadingFade) return false;

            return true;
        }

        private void Update()
        {
            if (Keybind.Value.IsDown())
                ShowInterface = !ShowInterface;
        }

        private void OnGUI()
        {
            if (!ShowInterface)
                return;

            foreach (var clothButton in _buttons)
            {
                if (GUI.Button(clothButton.Position, clothButton.Text))
                    clothButton.NextState();
            }

            GUILayout.BeginArea(_accesorySlotsRect);
            {
                var showAccessory = _chaCtrl.fileStatus.showAccessory;
                if (showAccessory.Length > 1)
                {
                    if (GUILayout.Button("All accs On"))
                        _chaCtrl.SetAccessoryStateAll(true);
                    GUILayout.Space(-5);
                    if (GUILayout.Button("All accs Off"))
                        _chaCtrl.SetAccessoryStateAll(false);
                }

                _accessorySlotsScrollPos = GUILayout.BeginScrollView(_accessorySlotsScrollPos);
                {
                    GUILayout.BeginVertical();
                    {
                        for (var j = 0; j < showAccessory.Length; j++)
                        {
                            if (_chaCtrl.nowCoordinate.accessory.parts[j].type != 120)
                                DrawAccesoryButton(j, showAccessory[j]);
                        }

                        if (_getMoreaccsData != null)
                        {
                            try
                            {
                                var data = _getMoreaccsData(_chaCtrl.chaFile);
                                if (data != null)
                                {
                                    var charAddDataTraverse = Traverse.Create(data);
                                    var showAccessories = charAddDataTraverse.Field("showAccessories").GetValue<List<bool>>();
                                    if (showAccessories != null)
                                    {
                                        var accObjects = charAddDataTraverse.Field("objAccessory").GetValue<List<GameObject>>();
                                        for (int i = 0; i < showAccessories.Count; i++)
                                        {
                                            // Filter out empty slots
                                            if (accObjects[i] != null)
                                                DrawAccesoryButton(i + 20, showAccessories[i]);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex) { Console.WriteLine(ex); }
                        }
                    }
                    GUILayout.EndVertical();
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndArea();

#if KK
            if (!MakerAPI.InsideMaker || ShowCoordinateButtons.Value)
            {
                const float coordWidth = 25f;

                for (var i = 0; i < 7; i++)
                {
                    var position = _buttons[i].Position;
                    position.x -= coordWidth + Margin;
                    position.width = coordWidth;
                    if (GUI.Button(position, (i + 1).ToString()))
                        _setCoordAction(i);
                }
            }
#endif
        }

        private void DrawAccesoryButton(int accIndex, bool isOn)
        {
            if (GUILayout.Button($"Slot {accIndex + 1}: {(isOn ? "On" : "Off")}"))
                _chaCtrl.SetAccessoryState(accIndex, !isOn);
            GUILayout.Space(-5);
        }

        private void SetupInterface()
        {
            var distanceFromRightEdge = Screen.width / 10f;
            var x = Screen.width - distanceFromRightEdge - Width - Margin;
            var windowRect = new Rect(x, Margin, Width, Height);

            // Clothing piece state buttons
            foreach (ChaFileDefine.ClothesKind kind in Enum.GetValues(typeof(ChaFileDefine.ClothesKind)))
            {
#if KK
                if (kind == ChaFileDefine.ClothesKind.shoes_outer) continue;
#endif
                _buttons.Add(new ClothButton(windowRect, kind, _chaCtrl));
                windowRect.y += Height;
            }
            // Invisible body
            if (MakerAPI.InsideMaker)
                _buttons.Add(new BodyButton(_chaCtrl, windowRect));

            // Accessories
            _accesorySlotsRect = _buttons.Last().Position;
            _accesorySlotsRect.x += 7;
            _accesorySlotsRect.width -= 7;
            _accesorySlotsRect.y += Height + Margin;
            _accesorySlotsRect.height = 300f;

#if KK
            // Coordinate change buttons
            var customControl = MakerAPI.GetMakerBase()?.customCtrl;
            if (customControl != null)
            {
                var coordDropdown = typeof(CustomControl).GetField("ddCoordinate", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(customControl);
                var coordProp = coordDropdown?.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public);
                _setCoordAction = newVal => coordProp?.SetValue(coordDropdown, newVal, null);
            }
            else
            {
                _setCoordAction = newVal => _chaCtrl.ChangeCoordinateTypeAndReload((ChaFileDefine.CoordinateType)newVal);
            }
#endif
        }
    }
}
