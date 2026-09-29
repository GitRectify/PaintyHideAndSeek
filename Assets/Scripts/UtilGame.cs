using System;
using System.Globalization;
using TMPro;
using UnityEngine;

public class UtilGame
{
    public UtilGame()
    {
    }

    // =========================================================
    // DATA FAMILY
    // key + "_" + child + "_" + Application.identifier
    // =========================================================

    public static int GetDataInt(string key, int defualt, string child)
    {
        string prefKey =
            key + "_" + child + "_" + Application.identifier;

        return PlayerPrefs.GetInt(prefKey, defualt);
    }

    public static void SetDataInt(string key, int value, string child)
    {
        string prefKey =
            key + "_" + child + "_" + Application.identifier;

        PlayerPrefs.SetInt(prefKey, value);
        PlayerPrefs.Save();
    }

    public static bool GetDataBool(
        string key,
        bool defualt,
        string child)
    {
        string prefKey =
            key + "_" + child + "_" + Application.identifier;

        int defaultValue = defualt ? 1 : 0;

        return PlayerPrefs.GetInt(
            prefKey,
            defaultValue) != 0;
    }

    public static void SetDataBool(
        string key,
        bool value,
        string child)
    {
        string prefKey =
            key + "_" + child + "_" + Application.identifier;

        PlayerPrefs.SetInt(
            prefKey,
            value ? 1 : 0);

        PlayerPrefs.Save();
    }

    public static void SetDataString(
        string key,
        string value,
        string child)
    {
        string prefKey =
            key + "_" + child + "_" + Application.identifier;

        PlayerPrefs.SetString(
            prefKey,
            value ?? "");

        PlayerPrefs.Save();
    }

    // Original parameter order preserved:
    // key, child, default
    public static string GetDataString(
        string key,
        string child,
        string defualt)
    {
        string prefKey =
            key + "_" + child + "_" + Application.identifier;

        return PlayerPrefs.GetString(
            prefKey,
            defualt);
    }

    public static void SetDataFloat(
        string key,
        float value,
        string child)
    {
        string prefKey =
            key + "_" + child + "_" + Application.identifier;

        PlayerPrefs.SetFloat(
            prefKey,
            value);

        PlayerPrefs.Save();
    }

    public static float GetDataFloat(
        string key,
        float defualt,
        string child)
    {
        string prefKey =
            key + "_" + child + "_" + Application.identifier;

        return PlayerPrefs.GetFloat(
            prefKey,
            defualt);
    }

    // Original method has no "child" parameter.
    public static void SetDataDouble(
        string key,
        double value)
    {
        string prefKey =
            key + "_" + Application.identifier;

        SetDoublePref(prefKey, value);
        PlayerPrefs.Save();
    }

    public static double GetDataDouble(
        string key,
        float defualt,
        string child)
    {
        string prefKey =
            key + "_" + child + "_" + Application.identifier;

        return GetDoublePref(
            prefKey,
            defualt);
    }

    public static bool isHashkeyData(
        string key,
        string child)
    {
        string prefKey =
            key + "_" + child + "_" + Application.identifier;

        return PlayerPrefs.HasKey(prefKey);
    }

    public static void DeleteKeyData(
        string key,
        string child)
    {
        string prefKey =
            key + "_" + child + "_" + Application.identifier;

        PlayerPrefs.DeleteKey(prefKey);
        PlayerPrefs.Save();
    }

    // =========================================================
    // PLAIN FAMILY
    // key + "_none_" + Application.identifier
    // =========================================================

    public static int GetInt(
        string key,
        int adefault)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        return PlayerPrefs.GetInt(
            prefKey,
            adefault);
    }

    public static void SetInt(
        string key,
        int value)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        PlayerPrefs.SetInt(
            prefKey,
            value);

        PlayerPrefs.Save();
    }

    public static float GetFloat(
        string key,
        float adefualt)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        return PlayerPrefs.GetFloat(
            prefKey,
            adefualt);
    }

    public static void SetFloat(
        string key,
        float value)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        PlayerPrefs.SetFloat(
            prefKey,
            value);

        PlayerPrefs.Save();
    }

    public static double GetDoubleU(
        string key,
        double adefualt)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        return GetDoublePref(
            prefKey,
            adefualt);
    }

    public static void SetDouble(
        string key,
        double value)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        SetDoublePref(
            prefKey,
            value);

        PlayerPrefs.Save();
    }

    public static bool GetBool(
        string key,
        bool adefualt)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        int defaultValue =
            adefualt ? 1 : 0;

        return PlayerPrefs.GetInt(
            prefKey,
            defaultValue) != 0;
    }

    public static void SetBool(
        string key,
        bool value)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        PlayerPrefs.SetInt(
            prefKey,
            value ? 1 : 0);

        PlayerPrefs.Save();
    }

    public static string GetString(
        string key,
        string adefualt)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        return PlayerPrefs.GetString(
            prefKey,
            adefualt);
    }

    public static void SetString(
        string key,
        string value)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        PlayerPrefs.SetString(
            prefKey,
            value ?? "");

        PlayerPrefs.Save();
    }

    public static bool isHashkey(string key)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        return PlayerPrefs.HasKey(prefKey);
    }

    public static void DeleteKey(string key)
    {
        string prefKey =
            key + "_none_" + Application.identifier;

        PlayerPrefs.DeleteKey(prefKey);
        PlayerPrefs.Save();
    }

    // =========================================================
    // INTERNET
    // =========================================================

    public bool HasInternet()
    {
        bool reachable =
            Application.internetReachability !=
            NetworkReachability.NotReachable;

        if (!reachable)
        {
            Debug.Log(
                "Error. Check internet connection!");
        }

        return reachable;
    }

    public static bool IsConnectionNetwork()
    {
        return
            Application.internetReachability !=
            NetworkReachability.NotReachable;
    }

    // =========================================================
    // DATE / TIME
    // =========================================================

    public static DateTime NewTimeNow(
        DateTime dateTime,
        float minuteNow)
    {
        int hour = dateTime.Hour;
        int minute = dateTime.Minute;
        int day = dateTime.Day;

        if (dateTime.Minute > 0)
        {
            int minuteAdd =
                minuteNow != float.PositiveInfinity
                    ? (int)minuteNow
                    : int.MinValue;

            minute =
                dateTime.Minute + minuteAdd;

            if (minute > 59)
            {
                hour++;
                minute = 0;
            }
        }

        if (hour > 24)
        {
            day++;
        }

        return new DateTime(
            dateTime.Year,
            dateTime.Month,
            day,
            hour,
            minute,
            0);
    }

    public static int getSecondsLeft(
        int hours,
        int minutes,
        int seconds)
    {
        DateTime now = DateTime.Now;

        DateTime target =
            new DateTime(
                now.Year,
                now.Month,
                now.Day,
                hours,
                minutes,
                seconds);

        DateTime now2 = DateTime.Now;

        int diff =
            (target.Second - now2.Second)
            + (target.Hour - now2.Hour) * 3600
            + (target.Minute - now2.Minute) * 60;

        Debug.Log(
            "DAY 02: " + diff);

        return diff < 1
            ? 0
            : diff + 2;
    }

    public static bool CheckedTimer(string date)
    {
        Debug.Log(
            "DATE : parse: " + date);

        DateTime target =
            DateTime.Parse(date);

        DateTime now =
            DateTime.Now;

        if (target.Day < now.Day)
        {
            Debug.Log(
                "DATE: " + now.Day);
        }
        else if (target.Month < now.Month)
        {
            Debug.Log(
                "DATE: " + now.Month);
        }
        else
        {
            if (now.Year <= target.Year)
            {
                return false;
            }

            Debug.Log(
                "DATE: " + now.Year);
        }

        return true;
    }

    // =========================================================
    // UI
    // =========================================================

    public static void SetTextColor(
        TextMeshProUGUI txt,
        string colors)
    {
        ColorUtility.TryParseHtmlString(
            colors,
            out Color color);

        txt.color = color;
    }

    // =========================================================
    // PLAYERPREFS DOUBLE SUPPORT
    //
    // PlayerPrefs does not support double directly.
    // Store double as invariant string to preserve precision.
    // =========================================================

    private static double GetDoublePref(
        string key,
        double defaultValue)
    {
        string defaultString =
            defaultValue.ToString(
                "R",
                CultureInfo.InvariantCulture);

        string storedValue =
            PlayerPrefs.GetString(
                key,
                defaultString);

        if (double.TryParse(
                storedValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double result))
        {
            return result;
        }

        return defaultValue;
    }

    private static void SetDoublePref(
        string key,
        double value)
    {
        PlayerPrefs.SetString(
            key,
            value.ToString(
                "R",
                CultureInfo.InvariantCulture));
    }
}