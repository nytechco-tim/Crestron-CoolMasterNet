﻿using System;
using System.Text;
using System.Collections.Generic;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;

namespace CoolMaster_NET_Controller {

    public static class Core {

        //===================// Members //===================//

        internal static string ipa;
        internal static int    port;
        internal static bool   okToConnect;
        internal static bool   waitForTx;

        internal static Dictionary<string, Zone> Zones;
        internal static List<string> MessageQueue;

        internal static CTimer pollTimer;
        internal static CTimer reconnectTimer;

        internal static TCPClient client;

        public delegate void ConnectionStatus(ushort status);
        public static ConnectionStatus ConnectionStatusEvent { get; set; }


        //===================// Constructor //===================//

        static Core() {

            Zones        = new Dictionary<string, Zone>();
            MessageQueue = new List<string>();

        }

        //===================// Methods //===================//

        //-------------------------------------//
        // FUNCTION: TCPClientSettings
        // Description: ...
        //-------------------------------------//

        public static void TCPClientSettings(string _ip, ushort _prt) {

            ipa  = _ip;
            port = _prt;
            okToConnect = true;

            client = new TCPClient(ipa, port, 10240);
            client.SocketStatusChange += new TCPClientSocketStatusChangeEventHandler(clientSocketChange);
            DeviceConnect();
            reconnectTimer = new CTimer(reconnectTimerHandler, 15000);

        }

        //-------------------------------------//
        // FUNCTION: RegisterZone
        // Description: ...
        //-------------------------------------//

        public static ushort RegisterZone (string _uid, Zone _zone) {

            if (!Zones.ContainsKey(_uid)) {
                Zones.Add(_uid, _zone);
                return 1;
            } else {
                return 0;
            }

        }

        //-------------------------------------//
        // FUNCTION: DeviceConnect
        // Description: ...
        //-------------------------------------//

        internal static void DeviceConnect () {

            try {

                client.ConnectToServerAsync(clientConnect);

            } catch (Exception _er) {

                ErrorLog.Error("Error connecting to CoolMaster device at address {0}: {1}", ipa, _er);

            }

        }

        //-------------------------------------//
        // FUNCTION: ParseData
        // Description: ...
        //-------------------------------------//

        internal static void ParseFeedback(string _data) {

            string[] lines = _data.Split('\x0D');
            Zone _zn;

            string uid;

            for (int i = 0; i < lines.Length; i++) {

                if (lines[i] == "\x0A")
                    continue;

                if(lines[i][0] == '\x0A')
                    lines[i] = lines[i].Substring(1);

                if(lines[i].IndexOf("L") >= 0) {

                    uid     = lines[i].Substring(0, 6);

                    if (Zones.ContainsKey(uid)) {
                        _zn       = Zones[uid];
                        _zn.CorF  = lines[i][15] == 'C' ? "C" : "F";
                        _zn.UpdateOnOff (lines[i].Substring(7, 3).Trim());

                        if (_zn.CorF == "F") {
                            if (!_zn.lockSetpoint) {
                                _zn.UpdateSetpoint (lines[i].Substring(11, 5).TrimStart('0'));
                            }
                            _zn.UpdateTemp       (lines[i].Substring(18, 5).TrimStart('0'));
                            _zn.UpdateFanSpeed   (lines[i].Substring(25, 4).Trim());
                            _zn.UpdateSystemMode (lines[i].Substring(30, 4).Trim());
                            _zn.UpdateDemand     (lines[i].Substring(42, 1) == "1" ? true : false);
                        } else if (_zn.CorF == "C") {
                            if (!_zn.lockSetpoint) {
                                _zn.UpdateSetpoint (lines[i].Substring(11, 4).TrimStart('0'));
                            }
                            _zn.UpdateTemp       (lines[i].Substring(17, 4).TrimStart('0'));
                            _zn.UpdateFanSpeed   (lines[i].Substring(23, 4).Trim());
                            _zn.UpdateSystemMode (lines[i].Substring(38, 4).Trim());
                            _zn.UpdateDemand     (lines[i].Substring(40, 1) == "1" ? true : false);
                        }

                        //_zn.Update();
                    }

                }
            }

        }

        //-------------------------------------//
        // FUNCTION: QueueCommand
        // Description: ...
        //-------------------------------------//

        public static void QueueCommand(string _cmd) {

            // Ignore new commands if controller is disconnected or command is blank
            if (client.ClientStatus != SocketStatus.SOCKET_STATUS_CONNECTED || _cmd == "")
                return;

            string command = _cmd + "\x0D\x0A";

            if (waitForTx) {
                MessageQueue.Add(command);
            } else {
                waitForTx = true;
                client.SendDataAsync(Encoding.ASCII.GetBytes(command), command.Length, clientDataTX);
            }

        }

        //===================// Event Handlers //===================//

        //-------------------------------------//
        // EVENTHANDLER: clientSocketChange
        // Description: ...
        //-------------------------------------//

        internal static void clientSocketChange(TCPClient _cli, SocketStatus _status) {

            if (_status != SocketStatus.SOCKET_STATUS_CONNECTED && okToConnect) {

                ConnectionStatusEvent(0);
                pollTimer.Stop();
                DeviceConnect();
                reconnectTimer = new CTimer(reconnectTimerHandler, 15000);

            } else if (_status == SocketStatus.SOCKET_STATUS_CONNECTED) {

                ConnectionStatusEvent(1);

            }

        }

        //-------------------------------------//
        // EVENTHANDLER: clientDataRX
        // Description: ...
        //-------------------------------------//

        internal static void clientDataRX(TCPClient _cli, int _bytes) {

            string data = Encoding.ASCII.GetString(_cli.IncomingDataBuffer, 0, _bytes);

            if (data != ">" && data != "")
                ParseFeedback(data);

            client.ReceiveDataAsync(clientDataRX);

        }

        //-------------------------------------//
        // EVENTHANDLER: clientDataTX
        // Description: ...
        //-------------------------------------//

        internal static void clientDataTX(TCPClient _cli, int _bytes) {

            if (MessageQueue.Count > 0) {
                if(MessageQueue[0] != "")
                    client.SendDataAsync(Encoding.ASCII.GetBytes(MessageQueue[0]), MessageQueue[0].Length, clientDataTX);
                MessageQueue.RemoveAt(0);
            } else {
                waitForTx = false;
            }

        }

        //-------------------------------------//
        // EVENTHANDLER: clientConnect
        // Description: ...
        //-------------------------------------//

        internal static void clientConnect (TCPClient _cli) {

            client.ReceiveDataAsync(clientDataRX);

            // Start poll timer
            pollTimer = new CTimer(pollTimerHandler, 2000);

        }

        //-------------------------------------//
        // EVENTHANDLER: pollTimerHandler
        // Description: ...
        //-------------------------------------//

        internal static void pollTimerHandler (object o) {
            
            // Send ls2 command
            if(client.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED) {
                byte[] data = new byte[5];
                client.SendDataAsync(Encoding.ASCII.GetBytes("ls2\x0D\x0A"), 5, clientDataTX);

                // Reset timer
                pollTimer = new CTimer(pollTimerHandler, 2000);
            }

        }

        //-------------------------------------//
        // EVENTHANDLER: reconnectTimerHandler
        // Description: ...
        //-------------------------------------//

        internal static void reconnectTimerHandler(object o) {

            if (client.ClientStatus != SocketStatus.SOCKET_STATUS_CONNECTED) {
                DeviceConnect();
                // Reset timer
                reconnectTimer = new CTimer(reconnectTimerHandler, 15000);
            }

        }

    } // End Core class

    public delegate void DelegateUshortString (ushort value1, SimplSharpString value2);
    public delegate void DelegateUshort (ushort value);
    public delegate void DelegateString (SimplSharpString value); 
    public delegate void DelegateUshortUshort (ushort value1, ushort value2);

}
