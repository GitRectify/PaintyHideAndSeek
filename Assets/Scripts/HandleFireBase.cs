using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase;
using Firebase.Analytics;
using Firebase.Extensions;
using Firebase.RemoteConfig;
using UnityEngine;

public class HandleFireBase : MonoBehaviour
{
    public static HandleFireBase Instance { get; private set; }

    // NOTE: raw int value 7 in the decompiled ctor — I'm not confident enough in the exact
    // ordinal mapping of Firebase's real DependencyStatus enum to assert a specific named member
    // (e.g. "UnavailableOther") here, unlike the Pending/Failure/Success values used in
    // FetchComplete below, which I'm more confident about. Left as a raw cast rather than a
    // guessed name.
    public DependencyStatus dependencyStatus = (DependencyStatus)7; // TODO: exact enum member unconfirmed

    // No custom ctor needed beyond the field initializer above.

    private void Awake()
    {
        if (Instance == null)
        {
            DontDestroyOnLoad(gameObject);
            Instance = this;
        }
        else if (Instance != this)
        {
            Destroy(gameObject);
        }
    }

    private void Start()
    {
        FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(OnCheckDependencies);
    }

    private void OnCheckDependencies(Task<DependencyStatus> task)
    {
        dependencyStatus = task.Result;
        Debug.Log("FIREBASE STATUS: " + dependencyStatus.ToString());

        if (dependencyStatus == DependencyStatus.Available)
        {
            InitializeFirebase();
            InitializeFirebase_Remote();
        }
        else
        {
            Debug.LogError("Could not resolve Analytics all Firebase dependencies: " + dependencyStatus.ToString());
        }
    }

    private void InitializeFirebase()
    {
        FirebaseAnalytics.SetAnalyticsCollectionEnabled(true);
        // NOTE: the property-name constant here is inferred from Firebase's real public API
        // (matching a raw offset read, not a named field in the decompile) — reasonable guess,
        // not independently confirmed by this decompile alone.
        FirebaseAnalytics.SetUserProperty(FirebaseAnalytics.UserPropertySignUpMethod, "Google");
    }

    private void InitializeFirebase_Remote()
    {
        // CORRECTION: the earlier draft had config_test_int boxed as `true` (bool) and
        // config_test_bool boxed as `(byte)0`. Tracing the actual boxed value sizes: the
        // "int" entry boxes a 4-byte value of 1 (a real int, not a bool), and the "bool" entry
        // boxes a 1-byte value of 0 (a real bool `false`) — the two were mismatched in the draft.
        var defaults = new Dictionary<string, object>
        {
            { "config_test_string", "default local string" },
            { "config_test_int",    1 },
            { "config_test_float",  1.0 },
            { "config_test_bool",   false },
        };

        FirebaseRemoteConfig remoteConfig = FirebaseRemoteConfig.DefaultInstance;
        remoteConfig.SetDefaultsAsync(defaults);

        Debug.Log("Remote config ready!");
        FetchDataAsync();
    }

    private Task FetchDataAsync()
    {
        Debug.Log("Fetching data...");
        FirebaseRemoteConfig remoteConfig = FirebaseRemoteConfig.DefaultInstance;
        return remoteConfig.FetchAsync(TimeSpan.Zero).ContinueWith(FetchComplete);
    }

    // CORRECTION: this has the exact same decompiled body as FetchDataAsync above — per this
    // project's established convention for confirmed exact-duplicate methods, implemented once
    // and forwarded to rather than re-duplicating the whole body a second time. (The earlier
    // draft duplicated the full body instead of forwarding.) FetchDataAsync is confirmed called
    // (from InitializeFirebase_Remote); FetchFireBase's own caller isn't in this dump.
    private Task FetchFireBase()
    {
        return FetchDataAsync();
    }

    private void FetchComplete(Task fetchTask)
    {
        if (fetchTask.IsCanceled)
        {
            Debug.Log("Fetch canceled.");
        }
        else if (fetchTask.IsFaulted)
        {
            Debug.Log("Fetch encountered an error.");
        }
        else if (fetchTask.IsCompleted)
        {
            Debug.Log("Fetch completed successfully!");
        }

        FirebaseRemoteConfig remoteConfig = FirebaseRemoteConfig.DefaultInstance;
        ConfigInfo info = remoteConfig.Info;

        switch (info.LastFetchStatus)
        {
            case LastFetchStatus.Pending:
                Debug.Log("Latest Fetch call still pending.");
                return;

            case LastFetchStatus.Failure:
                if (info.LastFetchFailureReason == FetchFailureReason.Throttled)
                {
                    Debug.Log("Fetch throttled until " + info.ThrottledEndTime.ToString());
                }
                else if (info.LastFetchFailureReason == FetchFailureReason.Error)
                {
                    Debug.Log("Fetch failed for unknown reason");
                }
                return;

            case LastFetchStatus.Success:
                remoteConfig.ActivateAsync();
                Debug.Log(string.Format("Remote data loaded and ready (last fetch time {0}).", info.FetchTime));
                return;
        }
    }

    // ====================== ANALYTICS LOGGING HELPERS ======================

    public void LogEventWithString(string eventName)
    {
        FirebaseAnalytics.LogEvent(eventName);
    }

    public void LogEventWithFloat(string eventName, string parameterName, float value)
    {
        FirebaseAnalytics.LogEvent(eventName, parameterName, value);
    }

    public void LogEventParameter(string eventName, Parameter param)
    {
        // Confirmed no-op — the decompiled body is literally empty (just `return;`), not a
        // TODO/unknown body.
    }

    public void LogCurrentScreen(string nameScreen, string screenClass)
    {
        // Confirmed no-op — same as LogEventParameter above.
    }

    public void LogLevelStart(int level)
    {
        // NOTE: EventLevelStart/ParameterLevel names inferred from Firebase's real public API
        // (raw offset reads in the decompile, not named fields) — same caveat as
        // UserPropertySignUpMethod above.
        FirebaseAnalytics.LogEvent(
            FirebaseAnalytics.EventLevelStart,
            new Parameter[] { new Parameter(FirebaseAnalytics.ParameterLevel, level) });
    }

    public void LogLevelEnd(int level)
    {
        FirebaseAnalytics.LogEvent(
            FirebaseAnalytics.EventLevelEnd,
            new Parameter[] { new Parameter(FirebaseAnalytics.ParameterLevel, level) });
    }

    public void LogEndEvent(string level, string status, string booster_speed, string booster_time,
        string booster_power, string booster_slow, string booster_perfect_anim,
        string booster_unhidden, string booster_players, string booster_steady)
    {
        FirebaseAnalytics.LogEvent("level_completed", new Parameter[]
        {
            new Parameter("status", status),
            new Parameter("level", level),
            new Parameter("booster_speed", booster_speed),
            new Parameter("booster_time", booster_time),
            new Parameter("booster_power", booster_power),
            new Parameter("booster_slow", booster_slow),
            new Parameter("booster_perfect_anim", booster_perfect_anim),
            new Parameter("booster_unhidden", booster_unhidden),
            new Parameter("booster_players", booster_players),
            new Parameter("booster_steady", booster_steady),
        });
    }
}