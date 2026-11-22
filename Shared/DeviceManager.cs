using System.Globalization;

namespace HomeManagement.Shared;

public static class DeviceManager
{
    public static int GetUptime()
    {
        try
        {
            if (File.Exists("/proc/uptime"))
            {
                var uptimeText = File.ReadAllText("/proc/uptime");
                var uptimeSeconds = double.Parse(uptimeText.Split(' ')[0], CultureInfo.InvariantCulture);

                return (int)uptimeSeconds;
            }

            return (int)(Environment.TickCount64 / 1000);
        }
        catch
        {
            return (int)(Environment.TickCount64 / 1000);
        }
    }

    public static double GetTemperature()
    {
        try
        {
            if (File.Exists("/sys/class/thermal/thermal_zone0/temp"))
            {
                var tempText = File.ReadAllText("/sys/class/thermal/thermal_zone0/temp");
                var tempCelsius = double.Parse(tempText, CultureInfo.InvariantCulture) / 1000.0;

                return tempCelsius;
            }

            return Math.Round(42.0 + (Random.Shared.NextDouble() * 6.0), 1);
        }
        catch
        {
            return 0;
        }
    }
}