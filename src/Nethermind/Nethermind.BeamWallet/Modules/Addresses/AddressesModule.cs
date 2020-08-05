//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;

namespace Nethermind.BeamWallet.Modules.Addresses
{
    internal class AddressesModule : IModule
    {
        private static readonly Regex _urlRegex = new Regex(@"^http(s)?://([\w-]+.)+[\w-]+(/[\w- ./?%&=])?",
            RegexOptions.Compiled);
        private static readonly Regex _addressRegex = new Regex("(0x)([0-9A-Fa-f]{40})", RegexOptions.Compiled);
        private Process _process;
        private Timer _timer;
        private Window _mainWindow;
        private int _processId;
        private Label _runnerOnInfo;
        private Label _runnerOffInfo;
        public event EventHandler<(string nodeAddress, string address, Process process)> AddressesSelected;

        public AddressesModule()
        {
            // if (!File.Exists(path))
            // {
            //     return;
            // }
            CreateWindow();
            CreateProcess();
            StartProcess();
        }

        private void CreateWindow()
        {
            _mainWindow = new Window("Beam Wallet")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
        }

        private void CreateProcess()
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "./Nethermind.Runner",
                    Arguments = "--config mainnet_beam --JsonRpc.Enabled true",
                    RedirectStandardOutput = true
                }
            };
        }

        private void StartProcess()
        {
            try
            {
                _process.Start();
                _processId = _process.Id;
                _timer = new Timer(Update, null, TimeSpan.Zero, TimeSpan.FromSeconds(7));
            }
            catch
            {
                AddRunnerInfo("Error with starting a Nethermind.Runner process.");
            }
        }

        private void Update(object state)
        {
            UpdateRunnerState();
        }

        private void UpdateRunnerState()
        {
            Process process = null;
            try
            {
                process = Process.GetProcessById(_processId);
                AddRunnerInfo("Nethermind Runner is running");
                return;
            }
            catch
            {
                // ignored
            }

            if (process is null)
            {
                if (_runnerOnInfo is {})
                {
                    _mainWindow.Remove(_runnerOnInfo);
                }

                _runnerOffInfo = new Label(3, 1, $"Nethermind Runner is stopped.. Please, wait for it to start.");
                _mainWindow.Add(_runnerOffInfo);
                _process.Start();
                _processId = _process.Id;
            }

            if (_runnerOffInfo is {})
            {
                _mainWindow.Remove(_runnerOffInfo);
            }

            _runnerOnInfo = new Label(3, 1, "Nethermind Runner is running.");
            _mainWindow.Add(_runnerOnInfo);
        }

        private void AddRunnerInfo(string info)
        {
            _runnerOnInfo = new Label(3, 1, $"{info}");
            _mainWindow.Add(_runnerOnInfo);
        }

        public Task<Window> InitAsync()
        {
            var nodeAddressLabel = new Label(3, 3, "Enter node address:");
            var nodeAddressTextField = new TextField(28, 3, 80, "http://localhost:8545");
            var addressLabel = new Label(3, 5, "Enter account address:");
            var addressTextField = new TextField(28, 5, 80, "");
            
            var okButton = new Button(28, 7, "OK");
            var quitButton = new Button(36, 7, "Quit");
            quitButton.Clicked = () =>
            {
                try
                {
                    _process.Kill();
                }
                catch
                {
                    Application.Top.Running = false;
                    Application.RequestStop();
                }
            };
            okButton.Clicked = () =>
            {
                var nodeAddressString = nodeAddressTextField.Text.ToString();
                
                if (string.IsNullOrWhiteSpace(nodeAddressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Node address is empty.");
                    return;
                }

                if (!_urlRegex.IsMatch(nodeAddressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Node address is invalid.");
                    return;
                }
                
                var addressString = addressTextField.Text.ToString();
                
                if (string.IsNullOrWhiteSpace(addressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Address is empty.");
                    return;
                }

                if (!_addressRegex.IsMatch(addressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Address is invalid.");
                    return;
                }
                
                AddressesSelected?.Invoke(this, (nodeAddressString, addressString, _process));
            };
            _mainWindow.Add(quitButton, nodeAddressLabel, nodeAddressTextField, addressLabel,
                addressTextField, okButton);

            return Task.FromResult(_mainWindow);
        }
    }
}
