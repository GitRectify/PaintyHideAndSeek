using UnityEngine;

public class CheckRateGame : MonoBehaviour
{
    private void OnEnable()
    {
        GameController controller =
            SingletonMonoBehavior<GameController>.Instance;

        if (controller == null)
            return;

        if (controller.btnRate != null)
        {
            controller.btnRate.SetActive(GameData.isRate == 0);
        }
    }
}