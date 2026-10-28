using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum DiceOutcome { None, Single, Double, Missile, Relocate }

// Solo el dado. Ponlo en un GameObject de la escena y asigna los campos en el Inspector.
public class DiceController : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI diceResultValue;
    [SerializeField] private Button rollButton;

    // Apagado: esa cara cuenta como disparo de 1 casilla.
    [SerializeField] private bool relocateEnabled = true;  // Reubicar un organismo

    public int RemainingShots { get; private set; }
    public bool HasRolled { get; private set; }
    public DiceOutcome CurrentOutcome { get; private set; } = DiceOutcome.None;

    // true cuando el disparo actual revienta un bloque 2x2
    public bool IsAreaShot => CurrentOutcome == DiceOutcome.Missile;
    public bool CanShoot => HasRolled && RemainingShots > 0;

    // Se dispara justo después de tirar el dado
    public event System.Action<DiceOutcome> OnRolled;

    private bool locked; // true durante el turno de la IA: el jugador no puede tirar

    // Modo online: el dado NO se tira aquí. El botón solo pide la tirada al servidor
    // (RollRequested) y el resultado llega después con ApplyServerRoll.
    public bool OnlineMode;
    public event System.Action RollRequested;

    private void Start()
    {
        if (rollButton != null) rollButton.onClick.AddListener(OnRollButtonPressed);
        UpdateUI();
    }

    public void SetLocked(bool value)
    {
        locked = value;
        UpdateUI();
    }

    private void OnRollButtonPressed()
    {
        if (locked) return;
        if (OnlineMode) { RollRequested?.Invoke(); return; }
        RollDice();
    }

    // Resultado del dado decidido por el servidor.
    public void ApplyServerRoll(DiceOutcome outcome, int shots)
    {
        HasRolled = true;
        CurrentOutcome = outcome;
        RemainingShots = shots;
        UpdateUI();
    }

    public void SetRemainingShots(int shots)
    {
        RemainingShots = shots;
        UpdateUI();
    }

    public const int Faces = 9;

    // Regla del dado separada de la interfaz: fácil de cambiar o de validar en servidor.
    //  1-4  -> 1 casilla   (44 %)
    //  5-7  -> 2 casillas  (33 %)
    //  8    -> Misil 2x2   (11 %)
    //  9    -> Reubicar    (11 %)
    public static DiceOutcome OutcomeFromFace(int face, bool relocateEnabled)
    {
        if (face <= 4) return DiceOutcome.Single;
        if (face <= 7) return DiceOutcome.Double;
        if (face == 8) return DiceOutcome.Missile;
        return relocateEnabled ? DiceOutcome.Relocate : DiceOutcome.Single;
    }

    public void RollDice()
    {
        if (HasRolled) return; // una tirada por turno

        HasRolled = true;
        int face = Random.Range(1, Faces + 1);
        CurrentOutcome = OutcomeFromFace(face, relocateEnabled);
        Debug.Log($"Dado: {face} -> {CurrentOutcome}");

        switch (CurrentOutcome)
        {
            case DiceOutcome.Single: RemainingShots = 1; break;
            case DiceOutcome.Double: RemainingShots = 2; break;
            case DiceOutcome.Missile: RemainingShots = 1; break; // 1 disparo, área 2x2
            default: RemainingShots = 0; break; // Relocate: sin disparos
        }

        UpdateUI();
        OnRolled?.Invoke(CurrentOutcome);
    }

    // Llámalo cada vez que el jugador dispara de verdad
    public void RegisterShot()
    {
        if (RemainingShots <= 0) return;
        RemainingShots--;
        UpdateUI();
    }

    // Llámalo al terminar el turno para poder volver a tirar
    public void ResetDice()
    {
        HasRolled = false;
        RemainingShots = 0;
        CurrentOutcome = DiceOutcome.None;
        UpdateUI();
    }

    private void UpdateUI()
    {
        if (rollButton != null) rollButton.interactable = !HasRolled && !locked;
        if (diceResultValue == null) return;

        if (!HasRolled) { diceResultValue.text = ""; return; }

        switch (CurrentOutcome)
        {
            case DiceOutcome.Relocate: diceResultValue.text = "Mover"; break;
            case DiceOutcome.Missile: diceResultValue.text = RemainingShots > 0 ? "2x2" : ""; break;
            default: diceResultValue.text = RemainingShots > 0 ? RemainingShots.ToString() : ""; break;
        }





    }
}