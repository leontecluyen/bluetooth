import bluetooth
import os

# Standard SPP UUID
UUID = "00001101-0000-1000-8000-00805F9B34FB"
STX = b'\x02'
ETX = b'\x03'

def start_server():
    server_sock = bluetooth.BluetoothSocket(bluetooth.RFCOMM)
    server_sock.bind(("", bluetooth.PORT_ANY))
    server_sock.listen(1)

    port = server_sock.getsockname()[1]
    bluetooth.advertise_service(server_sock, "SyncLogServer",
                                service_id=UUID,
                                service_classes=[UUID, bluetooth.SERIAL_PORT_CLASS],
                                profiles=[bluetooth.SERIAL_PORT_PROFILE])

    print(f"Waiting for connection on RFCOMM channel {port}...")

    try:
        while True:
            client_sock, client_info = server_sock.accept()
            print(f"Accepted connection from {client_info}")

            data_buffer = b""
            try:
                while True:
                    data = client_sock.recv(1024)
                    if not data:
                        break
                    data_buffer += data

                    # Check for packet framed by STX and ETX
                    if STX in data_buffer and ETX in data_buffer:
                        start = data_buffer.find(STX)
                        end = data_buffer.find(ETX, start)
                        if start != -1 and end != -1:
                            payload = data_buffer[start+1:end]
                            print("Received CSV Payload:")
                            csv_text = payload.decode('utf-8')
                            print(csv_text)

                            # Save to file
                            with open("received_logs.csv", "a", encoding="utf-8") as f:
                                f.write(csv_text)

                            # Clear processed data from buffer
                            data_buffer = data_buffer[end+1:]
                            print("Saved to received_logs.csv")

            except IOError:
                pass

            print("Client disconnected.")
            client_sock.close()
    except KeyboardInterrupt:
        pass
    finally:
        server_sock.close()
        print("Server stopped.")

if __name__ == "__main__":
    # Note: Requires PyBluez or similar and a compatible Bluetooth stack on Windows
    # If PyBluez is hard to install, use a simpler serial library if the OS maps BT to COM.
    start_server()
