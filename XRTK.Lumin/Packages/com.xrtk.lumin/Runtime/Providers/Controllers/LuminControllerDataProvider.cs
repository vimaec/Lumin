﻿// Copyright (c) XRTK. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using AOT;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using XRTK.Attributes;
using XRTK.Definitions.Devices;
using XRTK.Definitions.Platforms;
using XRTK.Definitions.Utilities;
using XRTK.Interfaces.InputSystem;
using XRTK.Lumin.Native;
using XRTK.Lumin.Profiles;
using XRTK.Providers.Controllers;
using XRTK.Services;
using XRTK.Utilities.Async;

namespace XRTK.Lumin.Providers.Controllers
{
    [RuntimePlatform(typeof(LuminPlatform))]
    [Guid("851006A2-0762-49AA-80A5-A01C9A8DBB58")]
    public class LuminControllerDataProvider : BaseControllerDataProvider
    {
        // We need to store an instance because the callbacks below need to be static.
        private static LuminControllerDataProvider _instance;

        /// <inheritdoc />
        public LuminControllerDataProvider(string name, uint priority, LuminControllerDataProviderProfile profile, IMixedRealityInputSystem parentService)
            : base(name, priority, profile, parentService)
        {
            if (_instance != null)
            {
                Debug.LogAssertion("It is expected that there will not be 2 Lumin Controller Data Providers");
            }
            _instance = this;
        }

        private readonly IntPtr statePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MlInput.MLInputControllerState)) * 2);
        private readonly MlInput.MLInputConfiguration inputConfiguration = MlInput.MLInputConfiguration.Default;
        private readonly MlController.MLControllerConfiguration controllerConfiguration = MlController.MLControllerConfiguration.Default;

        /// <summary>
        /// Dictionary to capture all active controllers detected
        /// </summary>
        private readonly Dictionary<byte, LuminController> activeControllers = new Dictionary<byte, LuminController>();

        private MlApi.MLHandle inputHandle;
        private MlApi.MLHandle controllerHandle;
        private MlInput.MLInputControllerCallbacksEx controllerCallbacksEx;
        private MlInput.MLInputControllerState[] controllerStates = new MlInput.MLInputControllerState[2];
        private MlController.MLControllerSystemState controllerSystemState;

        /// <inheritdoc />
        public override void Enable()
        {
            if (!Application.isPlaying) { return; }

            if (!inputHandle.IsValid)
            {
                if (MlInput.MLInputCreate(inputConfiguration, ref inputHandle).IsOk)
                {
                    controllerCallbacksEx.on_connect += OnConnect;
                    controllerCallbacksEx.on_disconnect += OnDisconnect;

                    if (MlInput.MLInputGetControllerState(inputHandle, statePointer).IsOk)
                    {
                        MlInput.MLInputControllerState.GetControllerStates(statePointer, ref controllerStates);
                    }
                    else
                    {
                        Debug.LogError($"Failed to update the controller input state!");
                    }

                    if (!MlInput.MLInputSetControllerCallbacksEx(inputHandle, controllerCallbacksEx, IntPtr.Zero).IsOk)
                    {
                        Debug.LogError("Failed to set controller callbacks!");
                    }
                }
                else
                {
                    Debug.LogError("Failed to create input tracker!");
                }
            }

            if (!controllerHandle.IsValid)
            {
                if (!MlController.MLControllerCreateEx(controllerConfiguration, ref controllerHandle).IsOk)
                {
                    Debug.LogError("Failed to create controller tracker!");
                }
                else
                {
                    MlController.MLControllerGetState(controllerHandle, ref controllerSystemState);
                    InitConnectedControllers();
                }
            }
        }

        private void InitConnectedControllers()
        {
            for (var i = 0; i < controllerStates.Length; i++)
            {
                if (!controllerStates[i].is_connected)
                    continue;

                GetController((byte)i);
            }
        }
        [MonoPInvokeCallback(typeof(MlInput.MLInputControllerCallbacksEx.on_connect_delegate))]
        static private async void OnConnect(byte id, IntPtr data)
        {
            await Awaiters.UnityMainThread;
            _instance.GetController(id);
        }
        [MonoPInvokeCallback(typeof(MlInput.MLInputControllerCallbacksEx.on_disconnect_delegate))]
        static private async void OnDisconnect(byte id, IntPtr data)
        {
            await Awaiters.UnityMainThread;
            _instance.RemoveController(id);
        }
        

        /// <inheritdoc />
        public override void Update()
        {
            base.Update();

            if (!Application.isPlaying) { return; }
            if (!inputHandle.IsValid) { return; }
            if (!controllerHandle.IsValid) { return; }

            if (MlInput.MLInputGetControllerState(inputHandle, statePointer).IsOk)
            {
                MlInput.MLInputControllerState.GetControllerStates(statePointer, ref controllerStates);
            }
            else
            {
                Debug.LogError($"Failed to update the controller input state!");
            }

            if (!MlController.MLControllerGetState(controllerHandle, ref controllerSystemState).IsOk)
            {
                Debug.LogError("Failed to get the controller system state!");
            }

            foreach (var controller in activeControllers)
            {
                controller.Value?.UpdateController(controllerStates[controller.Key], controllerSystemState.controller_state[controller.Key]);
            }
        }

        /// <inheritdoc />
        public override void Disable()
        {
            if (!Application.isPlaying) { return; }

            if (controllerHandle.IsValid)
            {
                controllerCallbacksEx.on_connect = null;
                controllerCallbacksEx.on_disconnect = null;
                controllerCallbacksEx.on_button_down = null;
                controllerCallbacksEx.on_button_up = null;

                if (!MlInput.MLInputSetControllerCallbacksEx(inputHandle, controllerCallbacksEx, IntPtr.Zero).IsOk)
                {
                    Debug.LogError("Failed to clear controller callbacks!");
                }

                if (!MlController.MLControllerDestroy(controllerHandle).IsOk)
                {
                    Debug.LogError($"Failed to destroy {nameof(MlController)} tracker!");
                }
            }

            if (inputHandle.IsValid)
            {
         
                if (!MlInput.MLInputDestroy(inputHandle).IsOk)
                {
                    Debug.LogError($"Failed to destroy the input tracker!");
                }
            }

            foreach (var activeController in activeControllers)
            {
                RemoveController(activeController.Key, false);
            }

            activeControllers.Clear();
        }

        /// <inheritdoc />
        protected override void OnDispose(bool finalizing)
        {
            if (finalizing)
            {
                Marshal.FreeHGlobal(statePointer);
            }

            base.OnDispose(finalizing);
        }

        private LuminController GetController(byte controllerId, bool addController = true)
        {
            //If a device is already registered with the ID provided, just return it.
            if (activeControllers.ContainsKey(controllerId))
            {
                var controller = activeControllers[controllerId];
                Debug.Assert(controller != null);
                return controller;
            }

            if (!addController) { return null; }

            LuminController detectedController;
            var handedness = (Handedness)(controllerId + 1);

            try
            {
                detectedController = new LuminController(this, TrackingState.NotTracked, handedness, GetControllerMappingProfile(typeof(LuminController), handedness));
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to create {nameof(LuminController)}!\n{e}");
                return null;
            }

            activeControllers.Add(controllerId, detectedController);
            AddController(detectedController);
            MixedRealityToolkit.InputSystem?.RaiseSourceDetected(detectedController.InputSource, detectedController);
            return detectedController;
        }

        private void RemoveController(byte controllerId, bool removeFromRegistry = true)
        {
            var controller = GetController(controllerId, false);

            if (controller != null)
            {
                MixedRealityToolkit.InputSystem?.RaiseSourceLost(controller.InputSource, controller);
            }

            if (removeFromRegistry)
            {
                RemoveController(controller);
                activeControllers.Remove(controllerId);
            }
        }
    }
}