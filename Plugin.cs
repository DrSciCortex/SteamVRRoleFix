// SPDX-FileCopyrightText: 2026 DrSciCortex
//
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Valve.VR;

namespace SteamVRRoleFix
{
    /// <summary>
    /// Resonite's renderer (SteamVRDriver) takes a device as a hand's controller only when SteamVR reports it
    /// connecting, and only if it holds that hand's role at that moment. SteamVR also moves the hand roles without a
    /// connection: a device that connected before it had the role, or while it was never-tracked, gets the role later
    /// (switching between controllers and hand tracking, or a driver such as CyberFinger's taking the hand back). The
    /// renderer then keeps reading the old device: the hand freezes and its input stops, until some device happens to
    /// reconnect. This plugin checks, every 100 ms, which device holds each hand role, and gives a new holder to the
    /// renderer's own device handling (RoleFollower).
    ///
    /// The renderer also turns a controller that connects without a role into a tracker, and a hand controller waiting
    /// for its hand isn't one: it sits on the hand, where Resonite then draws a tracker model. Controllers with a hand
    /// role hint are kept from becoming trackers (NoHandTrackers), and a hand's controller loses any tracker it has.
    ///
    /// A controller the hand switches away from has its inputs cleared (ClearOldInputs): Resonite would otherwise keep
    /// any button that was held at the switch held.
    /// </summary>
    [BepInPlugin(Guid, "SteamVRRoleFix", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.drscicortex.steamvrrolefix";
        public const string Version = "0.3.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            string missing = RoleFollower.Resolve();
            if (missing != null)
            {
                Log.LogError($"Resonite's renderer has changed ({missing} not found): not patching");
                return;
            }
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"SteamVRRoleFix {Version}: Resonite's hands follow SteamVR's hand roles, hand controllers " +
                        "never become trackers, and a controller switched away from leaves no input held");
        }
    }

    [HarmonyPatch]
    internal static class RoleFollower
    {
        // How often the role holders are checked (on the pose update that follows).
        private const float CheckSeconds = 0.1f;
        // A new role holder must keep the role this long first: a handover can pass the role through other devices.
        private const float SettleSeconds = 0.25f;
        // While the renderer still reads another device: retry this often, then less often for a device it refuses.
        private const float RetrySeconds = 1f, SlowRetrySeconds = 10f;
        private const int FastAttempts = 3;

        private static Type s_driverType;
        private static FieldInfo s_leftData, s_rightData, s_trackers, s_trackerIndex;
        private static PropertyInfo s_index;
        private static MethodInfo s_onDeviceConnected, s_removeTracker;

        internal static MethodInfo TrackerConnected { get; private set; }

        private sealed class Side
        {
            public readonly ETrackedControllerRole Role;
            public readonly FieldInfo Data;
            public uint Holder = OpenVR.k_unTrackedDeviceIndexInvalid;
            public float Since, NextTry;
            public int Attempts;
            public Side(ETrackedControllerRole role, FieldInfo data) { Role = role; Data = data; }
        }

        private static Side[] s_sides;
        private static float s_nextCheck;
        private static readonly HashSet<string> s_reported = new HashSet<string>();
        private static readonly HashSet<string> s_removing = new HashSet<string>();  // trackers queued for removal

        /// <summary>The renderer's members this needs; null when all are there, else the first missing one.</summary>
        internal static string Resolve()
        {
            s_driverType = AccessTools.TypeByName("SteamVRDriver");
            if (s_driverType == null) return "SteamVRDriver";
            s_leftData = AccessTools.Field(s_driverType, "LeftData");
            s_rightData = AccessTools.Field(s_driverType, "RightData");
            if (s_leftData == null || s_rightData == null) return "SteamVRDriver.LeftData/RightData";
            s_index = AccessTools.Property(s_leftData.FieldType, "Index");
            if (s_index == null) return s_leftData.FieldType.Name + ".Index";
            s_onDeviceConnected = AccessTools.Method(s_driverType, "OnDeviceConnected", new[] { typeof(int), typeof(bool) });
            if (s_onDeviceConnected == null) return "SteamVRDriver.OnDeviceConnected(int, bool)";
            if (AccessTools.Method(s_driverType, "OnNewPoses") == null) return "SteamVRDriver.OnNewPoses";
            s_trackers = AccessTools.Field(s_driverType, "trackers");
            Type tracker = AccessTools.Inner(s_driverType, "Tracker");
            s_trackerIndex = tracker != null ? AccessTools.Field(tracker, "deviceIndex") : null;
            if (s_trackers == null || s_trackerIndex == null) return "SteamVRDriver.trackers/Tracker.deviceIndex";
            s_removeTracker = AccessTools.Method(s_driverType, "RemoveTracker", new[] { typeof(string) });
            if (s_removeTracker == null) return "SteamVRDriver.RemoveTracker(string)";
            TrackerConnected = AccessTools.Method(s_driverType, "TrackerConnected",
                                                  new[] { typeof(int), typeof(string), typeof(string) });
            if (TrackerConnected == null) return "SteamVRDriver.TrackerConnected(int, string, string)";
            s_sides = new[]
            {
                new Side(ETrackedControllerRole.LeftHand, s_leftData),
                new Side(ETrackedControllerRole.RightHand, s_rightData),
            };
            return null;
        }

        private static MethodBase TargetMethod() => AccessTools.Method(s_driverType, "OnNewPoses");

        private static void Postfix(object __instance, TrackedDevicePose_t[] poses)
        {
            try
            {
                float now = Time.unscaledTime;
                if (now < s_nextCheck) return;
                s_nextCheck = now + CheckSeconds;
                CVRSystem system = OpenVR.System;
                if (system == null || poses == null) return;
                foreach (Side side in s_sides)
                    Follow(__instance, side, system, poses, now);
                DropHandTrackers(__instance);
            }
            catch (Exception e)
            {
                ReportOnce("OnNewPoses", e);
            }
        }

        private static void Follow(object driver, Side side, CVRSystem system, TrackedDevicePose_t[] poses, float now)
        {
            uint holder = system.GetTrackedDeviceIndexForControllerRole(side.Role);
            object data = side.Data.GetValue(driver);
            int current = data != null ? (int)s_index.GetValue(data, null) : -1;
            bool usable = holder != OpenVR.k_unTrackedDeviceIndexInvalid && holder < poses.Length &&
                          poses[holder].bDeviceIsConnected;
            if (!usable || holder == current)
            {
                side.Holder = OpenVR.k_unTrackedDeviceIndexInvalid;
                return;
            }

            if (holder != side.Holder)
            {
                side.Holder = holder;
                side.Since = now;
                side.NextTry = now + SettleSeconds;
                side.Attempts = 0;
            }
            if (now < side.NextTry) return;
            side.Attempts++;
            side.NextTry = now + (side.Attempts < FastAttempts ? RetrySeconds : SlowRetrySeconds);

            // The renderer skips never-tracked devices itself: nothing to hand it.
            ETrackedPropertyError err = ETrackedPropertyError.TrackedProp_Success;
            if (system.GetBoolTrackedDeviceProperty(holder, ETrackedDeviceProperty.Prop_NeverTracked_Bool, ref err)) return;

            Plugin.Log.LogInfo($"{side.Role}: SteamVR gives it to device {holder}, Resonite still reads device " +
                               $"{current} (for {now - side.Since:0.00} s): registering device {holder}");
            s_onDeviceConnected.Invoke(driver, new object[] { (int)holder, true });
        }

        // A hand's controller can't also be a tracker. The renderer removes a new controller's tracker by serial, and
        // SteamVR may report another serial for the device by then (controller emulation): this goes by device index.
        private static void DropHandTrackers(object driver)
        {
            var trackers = s_trackers.GetValue(driver) as IDictionary;
            if (trackers == null || trackers.Count == 0)
            {
                s_removing.Clear();
                return;
            }
            int left = IndexOf(s_leftData.GetValue(driver)), right = IndexOf(s_rightData.GetValue(driver));
            List<string> drop = null;
            foreach (DictionaryEntry entry in trackers)
            {
                int index = (int)s_trackerIndex.GetValue(entry.Value);
                if (index >= 0 && (index == left || index == right))
                    (drop ??= new List<string>()).Add((string)entry.Key);
            }
            s_removing.RemoveWhere(serial => !trackers.Contains(serial));
            if (drop == null) return;
            foreach (string serial in drop)
            {
                if (!s_removing.Add(serial)) continue;  // queued: the renderer removes it on its next state update
                Plugin.Log.LogInfo($"{serial} is a tracker on a device that is a hand's controller: removing the tracker");
                s_removeTracker.Invoke(driver, new object[] { serial });
            }
        }

        private static int IndexOf(object data) => data != null ? (int)s_index.GetValue(data, null) : -1;

        internal static void ReportOnce(string where, Exception e)
        {
            if (s_reported.Add(where))
                Plugin.Log.LogError($"{where} failed (reported once): {e}");
        }
    }

    /// <summary>
    /// When a hand switches controllers, the renderer stops reading the old one but keeps sending its last input state,
    /// and Resonite combines every controller's inputs on a side. A button held at the switch stays held: a Touch
    /// controller's dash button held this way keeps X/A from opening the dash, and makes a double X toggle UI edit
    /// mode. The renderer marks the old controller inactive (ClearActiveStatus); this also clears its inputs.
    /// </summary>
    [HarmonyPatch]
    internal static class ClearOldInputs
    {
        private static FieldInfo s_controller, s_deviceId;
        private static readonly Dictionary<Type, FieldInfo[]> s_inputs = new Dictionary<Type, FieldInfo[]>();

        private static bool Prepare()
        {
            Type data = AccessTools.TypeByName("SteamControllerData");
            s_controller = data != null ? AccessTools.Field(data, "Controller") : null;
            if (s_controller == null || AccessTools.Method(data, "ClearActiveStatus") == null)
            {
                Plugin.Log.LogError("Resonite's renderer has changed (SteamControllerData.Controller/ClearActiveStatus " +
                                    "not found): not clearing old controllers' inputs");
                return false;
            }
            s_deviceId = AccessTools.Field(s_controller.FieldType, "deviceID");
            return true;
        }

        private static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("SteamControllerData"), "ClearActiveStatus");

        private static void Postfix(object __instance)
        {
            try
            {
                object controller = s_controller.GetValue(__instance);
                if (controller == null) return;
                List<string> held = null;
                foreach (FieldInfo input in InputsOf(controller.GetType()))
                {
                    object idle = Activator.CreateInstance(input.FieldType);
                    if (Equals(input.GetValue(controller), idle)) continue;
                    (held ??= new List<string>()).Add(input.Name);
                    input.SetValue(controller, idle);
                }
                if (held != null)
                    Plugin.Log.LogInfo($"{s_deviceId?.GetValue(controller) ?? controller.GetType().Name} is no longer " +
                                       $"read: cleared its inputs ({string.Join(", ", held.ToArray())})");
            }
            catch (Exception e)
            {
                RoleFollower.ReportOnce("ClearActiveStatus", e);
            }
        }

        // The input fields: the value fields each controller type adds to VR_ControllerState (whose own fields are the
        // device, pose and battery), less enums such as the Touch model.
        private static FieldInfo[] InputsOf(Type type)
        {
            if (s_inputs.TryGetValue(type, out FieldInfo[] inputs)) return inputs;
            var found = new List<FieldInfo>();
            for (Type t = type; t != null && t != s_controller.FieldType; t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (f.FieldType.IsValueType && !f.FieldType.IsEnum)
                        found.Add(f);
            return s_inputs[type] = found.ToArray();
        }
    }

    /// <summary>
    /// The renderer maps a controller that connects without a role as a tracker. A controller with a hand role hint
    /// that has no role yet is waiting for its hand (a CyberFinger taking the hand back, the headset's hand tracking
    /// while CyberFingers or controllers hold the hand), not a tracker: it sits on the hand, where Resonite would draw a
    /// tracker model. Those are kept out; RoleFollower registers them once they hold the role.
    /// </summary>
    [HarmonyPatch]
    internal static class NoHandTrackers
    {
        private static readonly HashSet<string> s_logged = new HashSet<string>();

        private static MethodBase TargetMethod() => RoleFollower.TrackerConnected;

        private static bool Prefix(int index, string serial)
        {
            try
            {
                if (!IsHandController(index)) return true;
                if (s_logged.Add(serial ?? index.ToString()))
                    Plugin.Log.LogInfo($"Device {index} ({serial}) is a hand controller without its hand yet: not " +
                                       "making it a tracker");
                return false;
            }
            catch (Exception e)
            {
                RoleFollower.ReportOnce("TrackerConnected", e);
                return true;
            }
        }

        private static bool IsHandController(int index)
        {
            CVRSystem system = OpenVR.System;
            if (system == null || index < 0) return false;
            if (system.GetTrackedDeviceClass((uint)index) != ETrackedDeviceClass.Controller) return false;
            ETrackedPropertyError err = ETrackedPropertyError.TrackedProp_Success;
            int hint = system.GetInt32TrackedDeviceProperty((uint)index, ETrackedDeviceProperty.Prop_ControllerRoleHint_Int32,
                                                            ref err);
            return err == ETrackedPropertyError.TrackedProp_Success &&
                   (hint == (int)ETrackedControllerRole.LeftHand || hint == (int)ETrackedControllerRole.RightHand);
        }
    }
}
