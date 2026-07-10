using System;
using System.Collections;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

public class RadioController : MonoBehaviourPunCallbacks, IOnEventCallback
{
    [Header("Radio")]
    [SerializeField] private AudioSource radioAudioSource;
    [SerializeField] private Collider radioAreaCollider;

    [Header("3D Sound")]
    [SerializeField] private float minDistance = 1f;
    [SerializeField] private float maxDistance = 15f;

    [Header("UI")]
    [SerializeField] private GameObject interactionUI;
    [SerializeField] private GameObject recordingUI;
    [SerializeField] private TextMeshProUGUI countdownText;

    [Header("Recording")]
    [SerializeField] private int recordDuration = 10;
    [SerializeField] private int sampleRate = 8000;
    [SerializeField] private float recordedVolumeBoost = 3f;
    [SerializeField] private float voicePitch = 0.7f;
    [SerializeField] private float echoAmount = 0.4f;
    [SerializeField] private float echoDelay = 0.08f;
    [SerializeField] private int echoRepeats = 3;

    private const byte AUDIO_EVENT_START = 50;
    private const byte AUDIO_EVENT_DATA = 51;
    private const byte AUDIO_EVENT_LOCK = 52;
    private const byte AUDIO_EVENT_UNLOCK = 53;

    private bool isInArea;
    private bool isRecording;
    private bool isLocked;
    private AudioClip recordedClip;
    private AudioClip staticClip;
    private string micDevice;

    public void SetVolume(float vol)
    {
        if (radioAudioSource != null)
            radioAudioSource.volume = vol;
    }

    private void Start()
    {
        if (interactionUI != null) interactionUI.SetActive(false);
        if (recordingUI != null) recordingUI.SetActive(false);

        if (radioAudioSource != null)
        {
            staticClip = radioAudioSource.clip;
            radioAudioSource.spatialBlend = 1f;
            radioAudioSource.rolloffMode = AudioRolloffMode.Linear;
            radioAudioSource.minDistance = minDistance;
            radioAudioSource.maxDistance = maxDistance;
        }
    }

    public override void OnEnable()
    {
        base.OnEnable();
        PhotonNetwork.AddCallbackTarget(this);
    }

    public override void OnDisable()
    {
        base.OnDisable();
        PhotonNetwork.RemoveCallbackTarget(this);
    }

    private void Update()
    {
        CheckArea();

        if (!isInArea || isLocked || isRecording) return;

        Keyboard kb = Keyboard.current;
        if (kb != null && kb.eKey.wasPressedThisFrame)
            StartRecording();
    }

    private void CheckArea()
    {
        if (radioAreaCollider == null) return;

        ToyController local = FindLocalPlayer();
        if (local == null) return;

        CharacterController cc = local.GetComponent<CharacterController>();
        bool wasInArea = isInArea;

        if (cc != null && cc.enabled)
            isInArea = radioAreaCollider.bounds.Intersects(cc.bounds);
        else
            isInArea = Vector3.Distance(local.transform.position, radioAreaCollider.bounds.center) < maxDistance;

        if (isInArea && !isLocked && !isRecording)
        {
            if (interactionUI != null) interactionUI.SetActive(true);
        }
        else
        {
            if (interactionUI != null) interactionUI.SetActive(false);
        }
    }

    private ToyController FindLocalPlayer()
    {
        foreach (ToyController tc in FindObjectsByType<ToyController>(FindObjectsSortMode.None))
        {
            if (tc.photonView.IsMine) return tc;
        }
        return null;
    }

    #region Recording

    private void StartRecording()
    {
        if (Microphone.devices.Length == 0) return;

        BroadcastLock();

        isRecording = true;
        isLocked = true;

        if (interactionUI != null) interactionUI.SetActive(false);
        if (recordingUI != null) recordingUI.SetActive(true);

        micDevice = Microphone.devices[0];
        recordedClip = Microphone.Start(micDevice, false, recordDuration, sampleRate);

        StartCoroutine(RecordingCountdown());
    }

    private IEnumerator RecordingCountdown()
    {
        int remaining = recordDuration;

        while (remaining > 0)
        {
            if (countdownText != null)
                countdownText.text = $"(00:{remaining:D2})";

            yield return new WaitForSeconds(1f);
            remaining--;
        }

        if (countdownText != null)
            countdownText.text = "(00:00)";

        Microphone.End(micDevice);

        if (recordingUI != null) recordingUI.SetActive(false);

        yield return new WaitForSeconds(0.2f);

        SendAudioToAll();
    }

    #endregion

    #region Send Audio

    private void SendAudioToAll()
    {
        if (recordedClip == null) return;

        float[] samples = new float[recordedClip.samples * recordedClip.channels];
        recordedClip.GetData(samples, 0);

        short[] compressed = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            compressed[i] = (short)(Mathf.Clamp(samples[i], -1f, 1f) * short.MaxValue);

        byte[] bytes = new byte[compressed.Length * 2];
        Buffer.BlockCopy(compressed, 0, bytes, 0, bytes.Length);

        int chunkSize = 30000;
        int totalChunks = Mathf.CeilToInt((float)bytes.Length / chunkSize);

        PhotonNetwork.RaiseEvent(AUDIO_EVENT_START,
            new object[] { sampleRate, recordedClip.channels, totalChunks, bytes.Length },
            new RaiseEventOptions { Receivers = ReceiverGroup.All },
            new SendOptions { DeliveryMode = DeliveryMode.Reliable });

        StartCoroutine(SendChunks(bytes, chunkSize, totalChunks));
    }

    private IEnumerator SendChunks(byte[] bytes, int chunkSize, int totalChunks)
    {
        for (int i = 0; i < totalChunks; i++)
        {
            int offset = i * chunkSize;
            int length = Mathf.Min(chunkSize, bytes.Length - offset);
            byte[] chunk = new byte[length];
            Array.Copy(bytes, offset, chunk, 0, length);

            PhotonNetwork.RaiseEvent(AUDIO_EVENT_DATA,
                new object[] { i, chunk },
                new RaiseEventOptions { Receivers = ReceiverGroup.All },
                new SendOptions { DeliveryMode = DeliveryMode.Reliable });

            yield return new WaitForSeconds(0.05f);
        }
    }

    #endregion

    #region Receive Audio

    private int expectedRate;
    private int expectedChannels;
    private int expectedChunks;
    private int expectedBytes;
    private byte[][] receivedChunks;
    private int receivedCount;

    public void OnEvent(EventData photonEvent)
    {
        switch (photonEvent.Code)
        {
            case AUDIO_EVENT_START:
                object[] startData = (object[])photonEvent.CustomData;
                expectedRate = (int)startData[0];
                expectedChannels = (int)startData[1];
                expectedChunks = (int)startData[2];
                expectedBytes = (int)startData[3];
                receivedChunks = new byte[expectedChunks][];
                receivedCount = 0;
                break;

            case AUDIO_EVENT_DATA:
                object[] chunkData = (object[])photonEvent.CustomData;
                int idx = (int)chunkData[0];
                byte[] chunk = (byte[])chunkData[1];

                if (receivedChunks != null && idx < receivedChunks.Length)
                {
                    receivedChunks[idx] = chunk;
                    receivedCount++;

                    if (receivedCount >= expectedChunks)
                        PlayReceivedAudio();
                }
                break;

            case AUDIO_EVENT_LOCK:
                isLocked = true;
                if (interactionUI != null) interactionUI.SetActive(false);
                break;

            case AUDIO_EVENT_UNLOCK:
                isLocked = false;
                isRecording = false;
                if (isInArea && interactionUI != null)
                    interactionUI.SetActive(true);
                break;
        }
    }

    private void PlayReceivedAudio()
    {
        byte[] fullBytes = new byte[expectedBytes];
        int pos = 0;
        for (int i = 0; i < receivedChunks.Length; i++)
        {
            if (receivedChunks[i] == null) continue;
            Array.Copy(receivedChunks[i], 0, fullBytes, pos, receivedChunks[i].Length);
            pos += receivedChunks[i].Length;
        }

        short[] compressed = new short[fullBytes.Length / 2];
        Buffer.BlockCopy(fullBytes, 0, compressed, 0, fullBytes.Length);

        float[] samples = new float[compressed.Length];
        for (int i = 0; i < compressed.Length; i++)
            samples[i] = Mathf.Clamp(compressed[i] / (float)short.MaxValue * recordedVolumeBoost, -1f, 1f);

        AudioClip clip = AudioClip.Create("RadioPlayback", samples.Length, expectedChannels, expectedRate, false);
        clip.SetData(samples, 0);

        if (echoAmount > 0f && echoRepeats > 0)
            samples = ApplyEcho(samples, expectedRate);

        if (voicePitch != 1f)
        {
            int newLen = (int)(samples.Length * voicePitch);
            float[] stretched = new float[newLen];
            for (int i = 0; i < newLen; i++)
            {
                float srcIdx = (float)i / voicePitch;
                int idx = (int)srcIdx;
                float frac = srcIdx - idx;
                if (idx + 1 < samples.Length)
                    stretched[i] = Mathf.Lerp(samples[idx], samples[idx + 1], frac);
                else if (idx < samples.Length)
                    stretched[i] = samples[idx];
            }
            samples = stretched;
        }

        AudioClip finalClip = AudioClip.Create("RadioPlayback", samples.Length, expectedChannels, expectedRate, false);
        finalClip.SetData(samples, 0);

        if (radioAudioSource != null)
        {
            radioAudioSource.Stop();
            radioAudioSource.loop = false;
            radioAudioSource.pitch = voicePitch;
            radioAudioSource.clip = finalClip;
            radioAudioSource.Play();
        }

        float actualDuration = (float)samples.Length / expectedRate / voicePitch;
        StartCoroutine(WaitForPlaybackEnd(actualDuration));
    }

    private float[] ApplyEcho(float[] input, int rate)
    {
        float[] output = new float[input.Length];
        Array.Copy(input, output, input.Length);

        int delaySamples = (int)(echoDelay * rate);

        for (int r = 1; r <= echoRepeats; r++)
        {
            int offset = delaySamples * r;
            float volume = Mathf.Pow(echoAmount, r);

            for (int i = 0; i < input.Length; i++)
            {
                int target = i + offset;
                if (target < output.Length)
                    output[target] += input[i] * volume;
            }
        }

        float maxVal = 0f;
        for (int i = 0; i < output.Length; i++)
            if (Mathf.Abs(output[i]) > maxVal) maxVal = Mathf.Abs(output[i]);
        if (maxVal > 1f)
            for (int i = 0; i < output.Length; i++)
                output[i] /= maxVal;

        return output;
    }

    private IEnumerator WaitForPlaybackEnd(float duration)
    {
        yield return new WaitForSeconds(duration + 0.5f);

        if (radioAudioSource != null)
        {
            radioAudioSource.pitch = 1f;
            radioAudioSource.clip = staticClip;
            radioAudioSource.loop = true;
            radioAudioSource.Play();
        }

        BroadcastUnlock();
    }

    #endregion

    #region Lock/Unlock Broadcast

    private void BroadcastLock()
    {
        PhotonNetwork.RaiseEvent(AUDIO_EVENT_LOCK, null,
            new RaiseEventOptions { Receivers = ReceiverGroup.All },
            new SendOptions { DeliveryMode = DeliveryMode.Reliable });
    }

    private void BroadcastUnlock()
    {
        PhotonNetwork.RaiseEvent(AUDIO_EVENT_UNLOCK, null,
            new RaiseEventOptions { Receivers = ReceiverGroup.All },
            new SendOptions { DeliveryMode = DeliveryMode.Reliable });
    }

    #endregion
}
