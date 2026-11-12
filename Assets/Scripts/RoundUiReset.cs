using UnityEngine;

// Every button, panel and control state back to how a fresh round starts, run by every round start
// (Single Play's and online) and on the way back to the menu. Without it a panel left open — the
// pose picker, the paint panel — and a button left on — the rotate pair, lying down, the free
// camera — carried over into the next match.
public static class RoundUiReset
{
    public static void ResetAll()
    {
        PoseSelectorUI poses = Object.FindAnyObjectByType<PoseSelectorUI>(FindObjectsInactive.Include);
        if (poses != null)
        {
            poses.Close();              // the panel shut, its gate button off
            poses.ClearSelection();     // standing in no pose
        }

        ChameleonPaint paint = Object.FindAnyObjectByType<ChameleonPaint>(FindObjectsInactive.Include);
        if (paint != null) paint.CloseDrawingNow();     // panel away at once, brush mode, no loupe

        JoystickMover mover = Object.FindAnyObjectByType<JoystickMover>(FindObjectsInactive.Include);
        if (mover != null) mover.ResetControls();       // rotate pair hidden, nothing held

        Mode1FirstPersonGun gun = Object.FindAnyObjectByType<Mode1FirstPersonGun>(FindObjectsInactive.Include);
        if (gun != null) gun.StandUp();                 // the lie-down button off, camera up

        CameraController cam = Object.FindAnyObjectByType<CameraController>(FindObjectsInactive.Include);
        if (cam != null)
        {
            cam.SetFreeLook(false);     // the scout camera button off
            cam.ClearSpotView();
            cam.SetProne(false);
        }

        WallClimber climber = Object.FindAnyObjectByType<WallClimber>(FindObjectsInactive.Include);
        if (climber != null) climber.ResetControls();   // off any wall, climb buttons released

        SettingsUI settings = Object.FindAnyObjectByType<SettingsUI>(FindObjectsInactive.Include);
        if (settings != null) settings.Hide();
    }
}
