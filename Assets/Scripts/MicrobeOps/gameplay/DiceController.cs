using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum DiceOutcome { None, Single, Double, Missile, Relocate, Reveal }

// Solo el dado. Ponlo en un GameObject de la escena y asigna los campos en el Inspector.
public class DiceController : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI diceResultValue;
    [SerializeField] private Button rollButton;

    // Apagado: esa cara cuenta como disparo de 1 casilla.
    [SerializeField] private bool relocateEnabled = true;  // Reubicar un organismo
    [SerializeField] private bool revealEnabled = false;   // Revelar (rayos X): aún sin implementar

    public int RemainingShots { get; private set; }
    public bool HasRolled { get; private set; }
    public DiceOutcome CurrentOutcome { get; private set; } = DiceOutcome.None;

    // true cuando el disparo actual revienta un bloque 2x2
    public bool IsAreaShot => CurrentOutcome == DiceOutcome.Missile;
    public bool CanShoot => HasRolled && RemainingShots > 0;

    // Se dispara justo después de tirar el dado
    public event System.Action<DiceOutcome> OnRolled;

    private bool locked; // true durante el turno de la IA: el jugador no puede tirar

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
        RollDice();
    }

    public const int Faces = 10;

    // Regla del dado separada de la interfaz: fácil de cambiar o de validar en servidor.
    //  1-4  -> 1 casilla   (40 %)
    //  5-7  -> 2 casillas  (30 %)
    //  8    -> Misil 2x2   (10 %)
    //  9    -> Reubicar    (10 %)
    //  10   -> Revelar     (10 %)
    public static DiceOutcome OutcomeFromFace(int face, bool relocateEnabled, bool revealEnabled)
    {
        if (face <= 4) return DiceOutcome.Single;
        if (face <= 7) return DiceOutcome.Double;
        if (face == 8) return DiceOutcome.Missile;
        if (face == 9) return relocateEnabled ? DiceOutcome.Relocate : DiceOutcome.Single;
        return revealEnabled ? DiceOutcome.Reveal : DiceOutcome.Single;
    }

    public void RollDice()
    {
        if (HasRolled) return; // una tirada por turno

        HasRolled = true;
        int face = Random.Range(1, Faces + 1);
        CurrentOutcome = OutcomeFromFace(face, relocateEnabled, revealEnabled);
        Debug.Log($"Dado: {face} -> {CurrentOutcome}");

        switch (CurrentOutcome)
        {
            case DiceOutcome.Single: RemainingShots = 1; break;
            case DiceOutcome.Double: RemainingShots = 2; break;
            case DiceOutcome.Missile: RemainingShots = 1; break; // 1 disparo, área 2x2
            default: RemainingShots = 0; break; // Relocate / Reveal: sin disparos
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
            case DiceOutcome.Reveal: diceResultValue.text = "Rayos X"; break;
            case DiceOutcome.Missile: diceResultValue.text = RemainingShots > 0 ? "2x2" : ""; break;
            default: diceResultValue.text = RemainingShots > 0 ? RemainingShots.ToString() : ""; break;
        }





    }
}