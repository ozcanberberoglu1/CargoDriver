using System.Collections;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Password prompt for locked rooms. When a player clicks Join on a private room, this panel opens;
/// if the typed password matches the room's password the client joins, otherwise a wrong-password
/// message appears and fades out. Back just closes the panel.
///
/// Note: the check is client-side (Photon rooms have no real password), matching the game's
/// existing design — good enough for keeping honest players out of a locked room.
/// </summary>
public class PrivateRoomJoinPanel : MonoBehaviour
{
    [SerializeField] private TMP_InputField passwordInput;
    [SerializeField] private TMP_Text infoText;   // "wrong password", off by default
    [SerializeField] private Button joinButton;
    [SerializeField] private Button backButton;
    [Tooltip("How long the wrong-password message stays before it fades.")]
    [SerializeField] private float infoHold = 4f;
    [SerializeField] private float infoFade = 1f;

    private RoomInfo targetRoom;
    private Coroutine infoRoutine;
    private bool wired;

    private void Awake() => Wire();

    private void Wire()
    {
        if (wired) return;
        wired = true;
        if (joinButton != null) joinButton.onClick.AddListener(OnJoinClicked);
        if (backButton != null) backButton.onClick.AddListener(Close);
    }

    /// <summary>Called by a room row when the player wants into a locked room.</summary>
    public void Open(RoomInfo room)
    {
        Wire();
        targetRoom = room;
        gameObject.SetActive(true);

        if (passwordInput != null) passwordInput.text = "";
        if (infoText != null) infoText.gameObject.SetActive(false);
    }

    public void Close()
    {
        if (infoRoutine != null) { StopCoroutine(infoRoutine); infoRoutine = null; }
        gameObject.SetActive(false);
    }

    private void OnJoinClicked()
    {
        if (targetRoom == null) return;
        if (targetRoom.PlayerCount >= targetRoom.MaxPlayers) return;

        string roomPassword = targetRoom.CustomProperties.TryGetValue("password", out object pw)
            ? pw as string : "";
        string entered = passwordInput != null ? passwordInput.text : "";

        if (entered == roomPassword)
        {
            PhotonNetwork.JoinRoom(targetRoom.Name);
            Close();
        }
        else
        {
            ShowWrongPassword();
        }
    }

    private void ShowWrongPassword()
    {
        if (infoText == null) return;
        if (infoRoutine != null) StopCoroutine(infoRoutine);
        infoRoutine = StartCoroutine(InfoRoutine());
    }

    private IEnumerator InfoRoutine()
    {
        infoText.gameObject.SetActive(true);
        Color c = infoText.color;
        c.a = 1f;
        infoText.color = c;

        yield return new WaitForSecondsRealtime(infoHold);

        float t = 0f;
        while (t < infoFade)
        {
            t += Time.unscaledDeltaTime;
            c.a = Mathf.Clamp01(1f - t / infoFade);
            infoText.color = c;
            yield return null;
        }

        infoText.gameObject.SetActive(false);
        infoRoutine = null;
    }
}
