# Azure IoT Edge Module High Availability Library #
## Overview: ##
This library enables two or more Azure IoT Edge Modules to communicate across the network to establish an Active / Passive relationship, where the Active Module would process task.  This library was modeled after the Microsoft Clustering Server (https://en.wikipedia.org/wiki/Microsoft_Cluster_Server) architecture to address resiliency and high availability.
By initializing this library as code in a C# Azure IoT Edge Module there are synchronous and asynchronous methods to determine the Active state which should perform work.  With configurable millisecond ‘heartbeat’ messages and retry, this library can be configured at the sub second level.
Unlike Microsoft Clustering Server, this library does not require dedicated hardware (network or storage), matching hardware, matching operating systems or dedicated networks.
## Simple to Use: ##
After including the library and creating a single IoTEdgeModuelHA object, simply call the ActiveAsync() or the Active() method to determine state and preform workload.  The following example is a simplified code sample with just 3 lines added to the default template:
```
namespace hamodule
{
    using System;
    using System.Runtime.Loader;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using IoTEdgeModuleHA;
    
    class Program
    {
        static int counter;
        static IoTEdgeModuleHA IoTEdgeModuleHA;
        
        static void Main(string[] args)
        {
            Init().Wait();
            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            WhenCancelled(cts.Token).Wait();
        }
        
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }
        
        static async Task Init()
        {
            MqttTransportSettings mqttSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only);
            ITransportSettings[] settings = { mqttSetting };
            // Open a connection to the Edge runtime
            ModuleClient ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("IoT Hub module client initialized.");
            
            IoTEdgeModuleHA IoTEdgeModuleHA = new IoTEdgeModuleHA(ioTHubModuleClient, udpPort:20000);
            
            while (true){
                System.Threading.Thread.Sleep(1000);
                
                await IoTEdgeModuleHA.ActiveAsync();
                
                Message myiotMessage = new Message(System.Text.Encoding.UTF8.GetBytes("{\"message\":\"hello\"}"));
                await ioTHubModuleClient.SendEventAsync("output1", myiotMessage);
            }
        }
```
## Deployment: ##
1.	Copy the [IoTEdgeHA.dll](https://github.com/ksaye/AzureIoTEdgeModuleHA/raw/master/csharp/IoTEdgeHA/IoTEdgeHA/bin/Release/netcoreapp3.1/IoTEdgeHA.dll)  file to your project.
2.	Add the following to your “.csproj” file:
    ```<ItemGroup>
    <Reference Include="IoTEdgeModuleHA">
      <HintPath>IoTEdgeHA.dll</HintPath>
    </Reference>
    </ItemGroup>
    ```
3.	Add a “**using IoTEdgeModuleHA;**” to your “.cs” file
4.	After the “ioTHubModuleClient.OpenAsync()” line add “**IoTEdgeModuleHA IoTEdgeModuleHA = new IoTEdgeModuleHA(ioTHubModuleClient, udpPort:2000, broadcastSubnet="192.168.15.0");**” to your “.cs” file
5.	In your normal loop in IoT Edge, add “**await IoTEdgeModuleHA.ActiveAsync();**” which will pause if not Active
6.	In your deployment template or via the Azure Portal, add **"createOptions": "{\"ExposedPorts\":{\"2000/udp\":{}},\"HostConfig\":{\"PortBindings\":{\"2000/udp\":[{\"HostPort\":\"2000\"}]}}}"** to expose the UDP port 2000.
## Configuration: ##
The IoTEdgeModuleHA object requires a ModuleClient (or DeviceClient) for initialization and can optionally be passed the following parameters to fine tune the CPU usage and recover time.  These parameters can either be provided when creating the IoTEdgeModuleHA object or passed as a desired property in the module TWIN.
|Parameter|Type (Default)|Notes
|--|--|--|
|udpPort|Integer (60000)|This is the UDP port that the EdgeModuleHA sends and receives messages.  NOTE: the IoT Edge Module, via a creationOption, needs to be configured where the host listens on this port on behalf of the module.|
|broadcastSubnet|String (“192.168.15.0”)|Because the IoT Edge Module runs in a container on a different network, EdgeModuleHA needs to know the external network that other IoT Edge systems are running on.  This network is assumed to be a 24 bit network.|
|probeIntervalMS|Integer (200)|How often in milliseconds UDP packets are sent on the network.
|failedProbeCount|Integer (3)|The total number of missed probes at the probeIntervalMS duration until the Active host is considered down and an election is forced.|

## Desired TWINs: ##
Using the desired TWIN of the IoT Edge Module, the same parameter can be passed as shown below:
```
{
    "properties": {
        "desired": {
            "IoTEdgeModuleHA": {
                "probeIntervalMS": 1000,
                "failedProbeCount": 3,
                "udpPort": 2000,
                "broadcastSubnet": "192.168.15.0"
            },
        },
        "reported": {
       }
    },
    "tags": {}
}
```
## Reported TWINs: ##
The following reported TWINs show the state of the IoTEdgeModuleHA.  In addition to the desired properties there are 4 additional properties:
|Property|Value|Notes|
|--|--|--|
|isActive|true or false|Indicates if this IoTEdgeModuleHA is in active state|
|bootTimeEPOCH|Integer EPOCH|When the module started, used in election criteria|
|lastElection|Date|When the last election happened|
|Peers|Delimeted string|Shows all the peers by: IoTEdgeGatewayId \| isActive \| bootTimeEPOCH \| lastSeen|
```
{
    "properties": {
        "desired": {
        },
        "reported": {
            "IoTEdgeModuleHA": {
                "isActive": false,
                "bootTimeEPOCH": 1592424685,
                "lastElection": "2020-06-17T19:07:27.1922825Z",
                "peers": "node2|True|1592419489|06/17/2020 20:11:25;node1|False|1592424685|06/17/2020 20:11:26;",
                "udpPort": 2000,
                "broadcastSubnet": "192.168.15.0",
                "probeIntervalMS": 1000,
                "failedProbeCount": 3
            }
        }
    },
    "tags": {}
}
```
## Exposing UDP Ports: ##
Adding the following createOptions will expose the UDP port when deploying:
```
"modules": {
          "hamodule": {
            "version": "1.0",
            "type": "docker",
            "status": "running",
            "restartPolicy": "always",
            "settings": {
              "image": "${MODULES.hamodule}",
              "createOptions": "{\"ExposedPorts\":{\"2000/udp\":{}},\"HostConfig\":{\"PortBindings\":{\"2000/udp\":[{\"HostPort\":\"2000\"}]}}}"
            }
          }
        }
```
## Failover Logic: ##
When a node fails to receive probes from the active host in failedProbeCount duration, it assumes the role of isActive and if other nodes are online will go into election mode.
## Election Logic: ##
When one or more node claim to be “Active” and election is forced.  This election is based on 1) the highest bootTimeEPOCH and if there is a tie in bootTimeEPOCH the gatewayDeviceID that is highest in the alphabet “a0001” wins over “b0001”.
## False Positives: ##
Configuring the failedProbeCount too low can cause failovers to happen under CPU load.  Testing and tuning should be considered based on need.
## Failover Test Results: ##
Shown below, either stopping or having a failure of a host or module can force an election in less than a second.
 ![testresults](https://github.com/ksaye/AzureIoTEdgeModuleHA/blob/master/images/one.png)
## Source Code: ##
This project contains all source code.
