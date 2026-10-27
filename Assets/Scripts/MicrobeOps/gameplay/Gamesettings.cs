// Guarda el modo elegido en el menú para que lo lea la escena de juego.
public enum GameMode { Local, VsAI }

public static class Gamesettings
{
    public static GameMode Mode = GameMode.Local;
    public static bool HasSelection;

    public static void Select(GameMode mode)
    {
        Mode = mode;
        HasSelection = true;
    }
}