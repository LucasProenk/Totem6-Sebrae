using UnityEngine;

// Esconde o cursor do mouse em qualquer cena, assim que o jogo inicia —
// totem sem mouse/teclado, o cursor nunca deve aparecer na tela.
public static class CursorOculto
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Ocultar()
    {
        Cursor.visible = false;
    }
}
