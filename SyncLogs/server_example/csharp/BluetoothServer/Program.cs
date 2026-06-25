using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace BluetoothSppServer
{
    class Program
    {
        static void Main(string[] args)
        {
            Guid serviceUuid = new Guid("00001101-0000-1000-8000-00805F9B34FB");
            BluetoothListener listener = new BluetoothListener(serviceUuid);
            listener.Start();
            Console.WriteLine("Bluetooth SPP Server started. Waiting for connection...");

            byte STX = 0x02;
            byte ETX = 0x03;

            while (true)
            {
                using (BluetoothClient client = listener.AcceptBluetoothClient())
                {
                    Console.WriteLine($"Connected to: {client.RemoteMachineName}");
                    using (Stream stream = client.GetStream())
                    {
                        byte[] buffer = new byte[1024];
                        MemoryStream ms = new MemoryStream();

                        int bytesRead;
                        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ms.Write(buffer, 0, bytesRead);
                            byte[] currentData = ms.ToArray();

                            // Search for STX and ETX
                            int stxIndex = Array.IndexOf(currentData, STX);
                            int etxIndex = Array.IndexOf(currentData, ETX);

                            if (stxIndex != -1 && etxIndex != -1 && etxIndex > stxIndex)
                            {
                                int payloadLength = etxIndex - stxIndex - 1;
                                byte[] payload = new byte[payloadLength];
                                Array.Copy(currentData, stxIndex + 1, payload, 0, payloadLength);

                                string csv = Encoding.UTF8.GetString(payload);
                                Console.WriteLine("Received CSV Data:");
                                Console.WriteLine(csv);

                                File.AppendAllText("received_logs_bt.csv", csv);

                                // Reset memory stream for next packet
                                ms = new MemoryStream();
                            }
                        }
                    }
                    Console.WriteLine("Client disconnected.");
                }
            }
        }
    }
}
