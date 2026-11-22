# Home Management — Device API & Monitoring Dashboard

This solution contains two .NET 10 web applications:
1. **`HomeManagementDeviceApi`** — Simulated IoT / home device backend API exposing `/info`, `/config`, `/command`, `/shutdown`, and `/restart`.
2. **`HomeManagementDashboard`** — Blazor Interactive Server **Device Monitoring Dashboard** that polls configured devices, displays the number of online/offline devices, shows real-time telemetry metrics (`UptimeSeconds`, `Temperature`, latency, host IP), and lets you trigger remote device actions.

---

## How to Build

```cmd
dotnet build HomeManagementDeviceApi.slnx
```

---

## How to Run

### 1. Start the Simulated Devices (`Device1` and `Device2`)

Open two terminals in the root directory and run:

```cmd
dotnet run --project HomeManagementDeviceApi.csproj --launch-profile Device1
```

```cmd
dotnet run --project HomeManagementDeviceApi.csproj --launch-profile Device2
```

* `Device1` listens on `http://localhost:1235` (and `https://localhost:1234`)
* `Device2` listens on `http://localhost:1237` (and `https://localhost:1236`)

You can verify their JSON endpoints directly at `http://localhost:1235/info` and `http://localhost:1237/info`.

### 2. Start the Device Monitoring Dashboard

Open a third terminal and run:

```cmd
dotnet run --project HomeManagementDashboard/HomeManagementDashboard.csproj
```

Then open **`http://localhost:5265`** in your browser.
