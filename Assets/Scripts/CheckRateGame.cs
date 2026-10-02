using UnityEngine;

// Re-verified against raw Ghidra output and the ARM64 code (RVA 0x221F974). Method list and
// accessibility match dump.cs (TypeDefIndex 9614); the class has no fields.
//
// Ghidra typed the GameController instance as Il2CppObject / MethodInfo, so its field accesses
// printed as "pIVar2[8].monitor" and "method_01[1].parameters"; the disassembly shows both are
// offset 0x88 = GameController.numberRate, and "pIVar2[7].monitor" is 0x78 =
// GameController.popupRate. GameData.isRate is a static int property (Ghidra's argument to its
// getter is garbage).
//
// Convention: where raw jumps to the NullReferenceException stub, the C# just dereferences
// naturally; explicit null checks are kept only where raw really skips.
public class CheckRateGame : MonoBehaviour
{
    // Counts how often this is shown; on the first time only, the rate popup appears unless the
    // player has already rated.
    private void OnEnable()
    {
        SingletonMonoBehavior<GameController>.Instance.numberRate++;
        if (SingletonMonoBehavior<GameController>.Instance.numberRate != 1) return;
        SingletonMonoBehavior<GameController>.Instance.popupRate.SetActive(GameData.isRate == 0);
    }
}
