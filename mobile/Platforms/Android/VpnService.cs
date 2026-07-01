using Android.App;
using Android.Content;
using Android.OS;

namespace ZapretUI_Mobile;

[Service(Name = "com.zapret.vpn.VpnService", Permission = "android.permission.BIND_VPN_SERVICE", Exported = false)]
[IntentFilter(new[] { "android.net.VpnService" })]
public class VpnService : global::Android.Net.VpnService
{
    private ParcelFileDescriptor? _vpnInterface;
    private NotificationManager? _notificationManager;
    private const int NOTIFICATION_ID = 1;
    private const string CHANNEL_ID = "zapret_vpn";

    public override void OnCreate()
    {
        base.OnCreate();
        _notificationManager = GetSystemService(NotificationService) as NotificationManager;
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP")
        {
            StopVpn();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        StartForeground(NOTIFICATION_ID, BuildNotification());
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        StopVpn();
        base.OnDestroy();
    }

    public override void OnRevoke()
    {
        StopVpn();
        base.OnRevoke();
    }

    public ParcelFileDescriptor Establish(VpnService.Builder builder)
    {
        _vpnInterface = builder.Establish();
        return _vpnInterface!;
    }

    public new bool Protect(int fd)
    {
        return base.Protect(fd);
    }

    public bool Protect(System.Net.Sockets.Socket socket)
    {
        return base.Protect(socket);
    }

    private void StopVpn()
    {
        try
        {
            _vpnInterface?.Close();
            _vpnInterface = null;
        }
        catch { }
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(CHANNEL_ID, "Zapret VPN", NotificationImportance.Low)
            {
                Description = "Zapret VPN Service"
            };
            _notificationManager?.CreateNotificationChannel(channel);
        }
    }

    private Notification BuildNotification()
    {
        var stopIntent = new Intent(this, typeof(VpnService));
        stopIntent.SetAction("STOP");
        var stopPendingIntent = PendingIntent.GetService(
            this, 0, stopIntent, PendingIntentFlags.Immutable);

        var builder = new Notification.Builder(this, CHANNEL_ID)
            .SetContentTitle("Zapret VPN")
            .SetContentText("VPN is active")
            .SetSmallIcon(global::Android.Resource.Drawable.IcMenuManage)
            .SetOngoing(true)
            .AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Stop", stopPendingIntent);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            builder.SetChannelId(CHANNEL_ID);
        }

        return builder.Build()!;
    }
}