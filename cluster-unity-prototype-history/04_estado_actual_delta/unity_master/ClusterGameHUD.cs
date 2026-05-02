using UnityEngine;

public class ClusterGameHUD : MonoBehaviour
{
    private ClusterSimulationManager manager;
    private GUIStyle panelStyle;
    private GUIStyle titleStyle;
    private GUIStyle bodyStyle;
    private GUIStyle gameOverStyle;

    public void Initialize(ClusterSimulationManager manager)
    {
        this.manager = manager;
    }

    private void OnGUI()
    {
        if (manager == null)
        {
            return;
        }

        EnsureStyles();

        GUILayout.BeginArea(new Rect(12f, 12f, 310f, 140f), panelStyle);
        GUILayout.Label("Cluster Survival", titleStyle);
        GUILayout.Label(string.Format("Tiempo: {0:0.0}s", manager.SurvivalSeconds), bodyStyle);
        GUILayout.Label(string.Format("Puntuacion: {0}", manager.Score), bodyStyle);
        GUILayout.Label(string.Format("Enemigos: {0}", manager.EnemyCount), bodyStyle);
        GUILayout.Label(string.Format("Workers: {0}/{1}", manager.OnlineWorkerCount, manager.TotalWorkerCount), bodyStyle);
        GUILayout.Label(string.Format("Red: TX {0:0.0} B/s | RX {1:0.0} B/s", manager.NetworkTxRate, manager.NetworkRxRate), bodyStyle);
        GUILayout.Label("Moverse: WASD o flechas", bodyStyle);
        GUILayout.EndArea();

        if (!manager.IsGameOver)
        {
            return;
        }

        GUI.Box(new Rect(0f, 0f, Screen.width, Screen.height), GUIContent.none);

        float overlayWidth = 360f;
        float overlayHeight = 120f;
        Rect overlay = new Rect(
            (Screen.width - overlayWidth) * 0.5f,
            (Screen.height - overlayHeight) * 0.5f,
            overlayWidth,
            overlayHeight
        );

        GUILayout.BeginArea(overlay, panelStyle);
        GUILayout.Label("Has sido alcanzado", gameOverStyle);
        GUILayout.Label(string.Format("Supervivencia final: {0:0.0}s", manager.SurvivalSeconds), bodyStyle);
        GUILayout.Label(string.Format("Puntuacion final: {0}", manager.Score), bodyStyle);
        if (manager.RestartReady)
        {
            GUILayout.Label(string.Format("Pulsa {0} para reiniciar", manager.RestartKey), bodyStyle);
        }
        else
        {
            GUILayout.Label(string.Format("Reinicio disponible en {0:0.0}s", manager.RestartDelayRemaining), bodyStyle);
        }
        GUILayout.EndArea();
    }

    private void EnsureStyles()
    {
        if (panelStyle != null)
        {
            return;
        }

        panelStyle = new GUIStyle(GUI.skin.box);
        panelStyle.alignment = TextAnchor.UpperLeft;
        panelStyle.padding = new RectOffset(14, 14, 12, 12);

        titleStyle = new GUIStyle(GUI.skin.label);
        titleStyle.fontSize = 20;
        titleStyle.fontStyle = FontStyle.Bold;

        bodyStyle = new GUIStyle(GUI.skin.label);
        bodyStyle.fontSize = 14;

        gameOverStyle = new GUIStyle(titleStyle);
        gameOverStyle.alignment = TextAnchor.MiddleCenter;
    }
}
