from flask import Flask, request, jsonify
import csv
import os

app = Flask(__name__)

@app.route('/api/sync', methods=['POST'])
def sync_logs():
    logs = request.json
    if not logs:
        return "No data", 400

    print(f"Received {len(logs)} logs via Wi-Fi REST API")

    file_exists = os.path.isfile('received_logs_wifi.csv')

    with open('received_logs_wifi.csv', 'a', newline='', encoding='utf-8') as f:
        if logs:
            # Use keys from the first log as header if file is new
            writer = csv.DictWriter(f, fieldnames=logs[0].keys())
            if not file_exists:
                writer.writeheader()
            writer.writerows(logs)

    return "", 200

if __name__ == '__main__':
    # Listen on all interfaces at port 8080
    app.run(host='0.0.0.0', port=8080)
