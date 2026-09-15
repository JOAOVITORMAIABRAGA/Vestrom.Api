namespace Vestrom.Api.Configuration;

public sealed class VestromOptions
{
    public string JwtKey { get; set; } = "";
    public int JwtMinutes { get; set; } = 480;
    public string PublicHostName { get; set; } = "Galaxy S9 Homelab";
    public string TermuxBatteryCommand { get; set; } = "/data/data/com.termux/files/usr/bin/termux-battery-status";
    public string[] CorsOrigins { get; set; } = Array.Empty<string>();
}
