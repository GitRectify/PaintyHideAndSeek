using System;
using UnityEngine;
using UnityEngine.Android;
using Unity.Notifications.Android;

public class NotificationScheduler : MonoBehaviour
{
    private void Start()
    {
        GetPermissions();
        AndroidNotificationCenter.CancelAllDisplayedNotifications();
        CreateNotificationChannel();
        ScheduleDailyNotifications();
    }

    private void GetPermissions()
    {
        #if UNITY_ANDROID && !UNITY_EDITOR
                using (AndroidJavaClass buildVersion = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    int sdkInt = buildVersion.GetStatic<int>("SDK_INT");

                    if (sdkInt < 33)
                    {
                        Debug.Log("POST_NOTIFICATIONS not required (Android < 13)");
                        return;
                    }
                }

                const string permission = "android.permission.POST_NOTIFICATIONS";

                if (!Permission.HasUserAuthorizedPermission(permission))
                {
                    Permission.RequestUserPermission(permission);
                }
        #endif
    }

    private void CreateNotificationChannel()
    {
        AndroidNotificationChannel channel = new AndroidNotificationChannel
        {
            Id = "default_channel",
            Name = "Default Channel",
            Description = "Generic notifications",
            Importance = Importance.Default
        };

        AndroidNotificationCenter.RegisterNotificationChannel(channel);
    }

    private void ScheduleDailyNotifications()
    {
        ScheduleNotification(
            "Good Morning!",
            "Wake up! The Hide & Seek match has started. Will you hide... or hunt today?",
            9, 0);

        ScheduleNotification(
            "Hey!",
            "Someone is searching for you! Can you stay hidden until the end?",
            16, 0);

        ScheduleNotification(
            "Good Evening!",
            "Everyone is escaping except you. Can you survive until the timer ends?",
            20, 0);
    }

    private void ScheduleNotification(string title, string text, int hour, int minute)
    {
        DateTime now = DateTime.Now;
        DateTime fireDateTime =
            new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);

        if (fireDateTime <= now)
        {
            fireDateTime = fireDateTime.AddDays(1);
        }

        AndroidNotification notification = new AndroidNotification
        {
            Title = title,
            Text = text,
            FireTime = fireDateTime,
            RepeatInterval = TimeSpan.FromDays(1)
        };

        // No RootManager / ad-state modification here.
        AndroidNotificationCenter.SendNotification(notification, "default_channel");

        Debug.Log($"Notification scheduled: {title} at {fireDateTime}");
    }
}
