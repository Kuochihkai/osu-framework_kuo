﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Configuration;
using osu.Framework.Input;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.Handlers.Joystick;
using osu.Framework.Input.Handlers.Keyboard;
using osu.Framework.Input.Handlers.Midi;
using osu.Framework.Input.Handlers.Mouse;
using osu.Framework.Input.Handlers.Tablet;
using osuTK;

namespace osu.Framework.Platform
{
    public abstract class DesktopGameHost : GameHost
    {
        private TcpIpcProvider ipcProvider;
        private readonly bool bindIPCPort;
        private Thread ipcThread;

        internal bool UseOsuTK { get; }

        protected DesktopGameHost(string gameName = @"", bool bindIPCPort = false, ToolkitOptions toolkitOptions = default, bool portableInstallation = false, bool useOsuTK = false)
            : base(gameName, toolkitOptions)
        {
            this.bindIPCPort = bindIPCPort;
            IsPortableInstallation = portableInstallation;
            UseOsuTK = useOsuTK;
        }

        protected sealed override Storage GetDefaultGameStorage()
        {
            if (IsPortableInstallation || File.Exists(Path.Combine(RuntimeInfo.StartupDirectory, FrameworkConfigManager.FILENAME)))
                return GetStorage(RuntimeInfo.StartupDirectory);

            return base.GetDefaultGameStorage();
        }

        public sealed override Storage GetStorage(string path) => new DesktopStorage(path, this);

        protected override void SetupForRun()
        {
            if (bindIPCPort)
                startIPC();

            base.SetupForRun();
        }

        protected override void SetupToolkit()
        {
            if (UseOsuTK)
                base.SetupToolkit();
        }

        private void startIPC()
        {
            Debug.Assert(ipcProvider == null);

            ipcProvider = new TcpIpcProvider();
            IsPrimaryInstance = ipcProvider.Bind();

            if (IsPrimaryInstance)
            {
                ipcProvider.MessageReceived += OnMessageReceived;

                ipcThread = new Thread(() => ipcProvider.StartAsync().Wait())
                {
                    Name = "IPC",
                    IsBackground = true
                };

                ipcThread.Start();
            }
        }

        public bool IsPortableInstallation { get; }

        public override void OpenFileExternally(string filename) => openUsingShellExecute(filename);

        public override void OpenUrlExternally(string url) => openUsingShellExecute(url);

        private void openUsingShellExecute(string path) => Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true //see https://github.com/dotnet/corefx/issues/10361
        });

        public override ITextInputSource GetTextInput() => Window == null ? null : new GameWindowTextInput(Window);

        protected override IEnumerable<InputHandler> CreateAvailableInputHandlers()
        {
            switch (Window)
            {
                case OsuTKWindow _:
                {
                    var defaultEnabled = new InputHandler[]
                    {
                        new OsuTKMouseHandler(),
                        new OsuTKKeyboardHandler(),
                        new OsuTKJoystickHandler(),
                        new MidiInputHandler(),
#if NET5_0
                        new OpenTabletDriverHandler()
#endif
                    };

                    var defaultDisabled = new InputHandler[]
                    {
                        new OsuTKRawMouseHandler(),
                    };

                    foreach (var h in defaultDisabled)
                        h.Enabled.Value = false;

                    return defaultEnabled.Concat(defaultDisabled);
                }

                default:
                {
                    var defaultEnabled = new InputHandler[]
                    {
                        new KeyboardHandler(),
                        new MouseHandler(),
                        new JoystickHandler(),
                        new MidiInputHandler(),
#if NET5_0
                        new OpenTabletDriverHandler()
#endif
                    };

                    var defaultDisabled = new InputHandler[]
                    {
                        new OsuTKRawMouseHandler(),
                    };

                    foreach (var h in defaultDisabled)
                        h.Enabled.Value = false;

                    return defaultEnabled.Concat(defaultDisabled);
                }
            }
        }

        public override Task SendMessageAsync(IpcMessage message) => ipcProvider.SendMessageAsync(message);

        protected override void Dispose(bool isDisposing)
        {
            ipcProvider?.Dispose();
            ipcThread?.Join(50);
            base.Dispose(isDisposing);
        }
    }
}
