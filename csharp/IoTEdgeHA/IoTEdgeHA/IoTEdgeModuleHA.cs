using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace IoTEdgeModuleHA
{
    public class IoTEdgeModuleHA
    {
        private ModuleClient moduleClient;
        public bool isActive = false;
        public int failedProbes;
        private int l_probeIntervalMS;
        public int bootTimeEPOCH;
        public int UDPPort;
        private DateTime lastActiveMessage;
        private UdpClient udpClient = new UdpClient();
        private DateTime lastElection = new DateTime(1970, 1, 1);
        private List<peerModule> peerModules = new List<peerModule>();
        private string peersString;
        private string monitoredSubnetNetwork;
        private string gatewayID;
        private JObject message;
        private JObject twinJSON;
        private TwinCollection stateTwin;

        public IoTEdgeModuleHA(ModuleClient client, int udpPort=60000, string broadcastSubnet="192.168.15.0", int probeIntervalMS=200, int failedProbeCount = 3)
        {
            moduleClient = client;
            stateTwin = moduleClient.GetTwinAsync().Result.Properties.Desired;
            if (stateTwin.Contains("IoTEdgeModuleHA"))
            {
                Console.WriteLine(stateTwin["IoTEdgeModuleHA"].ToString());
                twinJSON = JObject.Parse(stateTwin["IoTEdgeModuleHA"].ToString());
            } else
            {
                Console.WriteLine(stateTwin.ToJson());
                twinJSON = JObject.Parse(stateTwin.ToJson());
            }

            if (twinJSON.ContainsKey("broadcastSubnet"))
            {
                monitoredSubnetNetwork = twinJSON["broadcastSubnet"].ToString().Substring(0, twinJSON["broadcastSubnet"].ToString().LastIndexOf(".") + 1);
            } else
            {
                monitoredSubnetNetwork = broadcastSubnet.Substring(0, broadcastSubnet.LastIndexOf(".") + 1);
            }

            if (twinJSON.ContainsKey("probeIntervalMS"))
            {
                l_probeIntervalMS = twinJSON["probeIntervalMS"].ToObject<int>();
            } else
            {
                l_probeIntervalMS = probeIntervalMS;
            }

            if (twinJSON.ContainsKey("failedProbeCount"))
            {
                failedProbes = twinJSON["failedProbeCount"].ToObject<int>();
            } else
            {
                failedProbes = failedProbeCount;
            }

            if (twinJSON.ContainsKey("udpPort"))
            {
                UDPPort = twinJSON["udpPort"].ToObject<int>();
            } else
            {
                UDPPort = udpPort;
            }
                        
            gatewayID = Environment.GetEnvironmentVariable("IOTEDGE_DEVICEID");

            Console.WriteLine("INFO: Initializing IoTEdgeModuleHA gatewayID:" + gatewayID + ", UDPPort:" + UDPPort + ", BroadcastSubnet:" + monitoredSubnetNetwork + ", probeMS:" + l_probeIntervalMS + ", probeCount:" + failedProbes);

            udpClient = new UdpClient(UDPPort);
            // random sleep in case 2 nodes boot at the exact same time
            System.Threading.Thread.Sleep(new Random().Next(0, (int)l_probeIntervalMS/3));
            bootTimeEPOCH = (int)(DateTime.Now - new DateTime(1970, 1, 1)).TotalSeconds;

            // start a thread to listen to UDP messages
            Thread processUDPThread = new Thread(processUDP);
            processUDPThread.Start();

            // start a timer to send state info
            System.Timers.Timer stateTimer = new System.Timers.Timer(l_probeIntervalMS);
            stateTimer.Elapsed += stateTick;
            stateTimer.Start();

            // start a timer to for election operations
            System.Timers.Timer electionTimer = new System.Timers.Timer(l_probeIntervalMS * failedProbes);
            electionTimer.Elapsed += electionTick;
            electionTimer.Start();
        }

        private async void electionTick(object sender, ElapsedEventArgs e)
        {
            if (isActive == true)
            {
                // already Active Node, no election needed
            } else if (lastActiveMessage < DateTime.UtcNow.AddMilliseconds(-1 * l_probeIntervalMS * failedProbes))
            {
                isActive = true;        // no updates from an Active Node, assuming the role
                stateTick(null, null);  // notifying other nodes
                await TWINUpdate();
            }
        }

        private async Task TWINUpdate()
        {
            twinJSON = new JObject();
            twinJSON["isActive"] = isActive;
            twinJSON["bootTimeEPOCH"] = bootTimeEPOCH;
            twinJSON["lastElection"] = lastElection;
            twinJSON["peers"] = peersString;
            twinJSON["udpPort"] = UDPPort;
            twinJSON["broadcastSubnet"] = monitoredSubnetNetwork;
            twinJSON["probeIntervalMS"] = l_probeIntervalMS;
            twinJSON["failedProbeCount"] = failedProbes;

            stateTwin = new TwinCollection("{\"IoTEdgeModuleHA\": " + twinJSON.ToString() + "}");
            await moduleClient.UpdateReportedPropertiesAsync(stateTwin);
        }

        private async void stateTick(object sender, ElapsedEventArgs e)
        {
            try
            {
                message = new JObject();
                message["isActive"] = isActive;
                message["gatewayID"] = gatewayID;
                message["bootTimeEPOCH"] = bootTimeEPOCH;

                byte[] messageByte = System.Text.Encoding.UTF8.GetBytes(message.ToString());
                for (int i=1; i < 255; i++)
                {
                    udpClient.Send(messageByte, messageByte.Length, new IPEndPoint(IPAddress.Parse(monitoredSubnetNetwork + i), UDPPort));
                }

            } catch (Exception er) {
                await moduleClient.SendEventAsync("error", new Message(System.Text.Encoding.UTF8.GetBytes(er.ToString())));
            }
        }

        public bool Active()
        {
            return isActive;
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async Task ActiveAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            while (true)
            {
                System.Threading.Thread.Sleep(l_probeIntervalMS);
                if (isActive)
                {
                    break;
                }
            }
        }

        private async void processUDP()
        {
            JObject message;
            UdpReceiveResult data;

            while (true)
            {
                try
                {
                    data = await udpClient.ReceiveAsync();
                    message = JObject.Parse(Encoding.UTF8.GetString(data.Buffer));

                    if (message.ContainsKey("isActive") && message.ContainsKey("gatewayID") && message["isActive"].Type == JTokenType.Boolean)
                    {
                        if (isActive == true &&
                            message["gatewayID"].ToString() != gatewayID &&
                            message["isActive"].ToObject<Boolean>() == true)
                        {
                            // we have a problem, 2 or more host claim to be the Active Primary, forcing an election
                            if (message.ContainsKey("bootTimeEPOCH"))
                            {
                                if (message["bootTimeEPOCH"].ToObject<int>() < bootTimeEPOCH)
                                {
                                    // sender has been running longer losing the election
                                    isActive = false;
                                } else if (message["bootTimeEPOCH"].ToObject<int>() > bootTimeEPOCH)
                                {
                                    // sender has NOT been running longer, this device wins the election
                                    isActive = true;
                                } else
                                {
                                    // startup time match, comparing gateway Names, lowest alphabetically wins
                                    if (String.Compare(message["gatewayID"].ToString(), gatewayID) < 0)
                                    {
                                        isActive = true;
                                    } else
                                    {
                                        isActive = false;
                                    }
                                }
                                lastElection = DateTime.UtcNow;
                                stateTick(null, null);
                                await TWINUpdate();
                            }
                        }
                        else if (isActive == false &&
                            message["isActive"].ToObject<Boolean>() == true)
                        {
                            lastActiveMessage = DateTime.UtcNow;
                        }

                        // adding this peer to the list of seen peers in the last hour
                        if (peerModules.FindAll(x => x.gatewayID == message["gatewayID"].ToString() &&
                            x.isActive == message["isActive"].ToObject<Boolean>() && 
                            x.lastSeen.AddHours(-1) < DateTime.UtcNow).Count == 0)
                        {
                            peerModules.RemoveAll(x => x.gatewayID == message["gatewayID"].ToString());
                            peerModules.Add(new peerModule()
                            {
                                gatewayID = message["gatewayID"].ToString(),
                                lastSeen = DateTime.UtcNow,
                                isActive = message["isActive"].ToObject<Boolean>(),
                                bootTimeEPOC = message["bootTimeEPOCH"].ToObject<int>()
                            });

                            peersString = "";
                            foreach (peerModule item in peerModules)
                            {
                                peersString += item.gatewayID + "|" + item.isActive + "|" + item.bootTimeEPOC + "|" + item.lastSeen.ToString() + ";";   // because TWINS don't support Arrays yet, convert List to String
                            }

                            await TWINUpdate();
                        }
                    }
                } catch (Exception er) {
                    await moduleClient.SendEventAsync("error", new Message(System.Text.Encoding.UTF8.GetBytes(er.ToString())));
                }
            }
        }
    }
    public class peerModule
    {
        public string gatewayID { get; set; }
        public DateTime lastSeen { get; set; }
        public int bootTimeEPOC { get; set; }
        public bool isActive { get; set; }
    }
}
