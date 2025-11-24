using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

// マイクから録音した音声を utopia-server の /api/stt に送って文字起こしし，
// 返ってきたテキストを UtopiaClient に渡して /api/ask_tts パイプラインに乗せるコンポーネント

[Serializable]
// /api/stt が返すJSONデータを受け取るためのクラス
public class SttResponse
{
    public string text;
    public string language;
    public float duration_sec;
    // segments や meta は今は使わないので定義しない
}

public class UtopiaSttClient : MonoBehaviour
{

    [Header("utopia-server (STT 用)")]
    [SerializeField]
    private string baseUrl = "http://192.168.0.79:5000";

    // STTの結果を渡す先のスクリプトをセット
    [Tooltip("文字起こし結果を渡す先の UtopiaClient")]
    [SerializeField]
    private UtopiaClient utopiaClient;

    // マイク指定（空欄ならOS規定）
    [Header("Microphone 設定")]
    [Tooltip("空欄ならデフォルトのマイクデバイスを使用")]
    [SerializeField]
    private string microphoneDeviceName = "";

    // 録音時の音質（1秒間のデータ数）
    [Tooltip("録音サンプリングレート（サーバ側では 16kHz にリサンプリングされる）")]
    [SerializeField]
    private int sampleRate = 16000;

    [Tooltip("最大録音長 (秒)。これを超えるとそれ以降は上書き")]
    [SerializeField]
    private int maxRecordLengthSec = 10;

    [Header("操作")]
    [Tooltip("このキーを押している間だけ録音する（テスト用）。後ほどXR入力に差し替え")]
    [SerializeField]
    private KeyCode recordKey = KeyCode.V;

    // コンソールにデバッグログを表示するかどうかのスイッチ
    [Header("Debug")]
    [SerializeField]
    private bool logDebug = true;

    // ---------- Private Variables ----------

    // 録音中の音声データを保持する変数
    private AudioClip _recordingClip; // 録音中のAudioClip
    private bool _isRecording; // 録音中フラグ
    private string _usedDeviceName; // 実際に使っているマイクデバイス名

    // XR Aボタンの前フレーム状態（長押し検出用）
    private bool _xrAWasPressed;

    // ---------- Private Methods ----------

    // 入力監視（キーボード ＆ XRボタン）
    private void Update()
    {
        // 1. キーボード
        if (Input.GetKeyDown(recordKey))
        {
            // 録音開始
            StartRecording();
        }

        if (Input.GetKeyUp(recordKey))
        {
            // 録音を停止してサーバに送信
            StopRecordingAndSend();
        }

        // 2. XRコントローラー Aボタン長押し (InputManagerLR 経由)
        bool xrAPressed = InputManagerLR.PrimaryButtonR();

        // 押し始めフレーム
        if (xrAPressed && !_xrAWasPressed)
        {
            if (logDebug)
                Debug.Log("[UtopiaSttClient] XR A button pressed → StartRecording()");
            StartRecording();
        }
        // 離したフレーム
        else if (!xrAPressed && _xrAWasPressed)
        {
            if (logDebug)
                Debug.Log("[UtopiaSttClient] XR A button released → StopRecordingAndSend()");
            StopRecordingAndSend();
        }

        _xrAWasPressed = xrAPressed;
    }

    // ---------- Public Methods ----------

    // マイク録音開始（外部からも呼べるよう public）
    public void StartRecording()
    {
         // 録音中なら何もしない
        if (_isRecording)
            return;

        // マイク名がinsperctorで空欄ならデフォルトデバイスを使う
        _usedDeviceName = string.IsNullOrWhiteSpace(microphoneDeviceName)
            ? null          // null だとデフォルトデバイス
            : microphoneDeviceName;

        // デバッグログを出力
        if (logDebug)
        {
            Debug.Log($"[UtopiaSttClient] StartRecording device={_usedDeviceName ?? "<default>"} " +
                      $"sampleRate={sampleRate}, maxSec={maxRecordLengthSec}");
        }

        // Unityのマイク録音開始（デバイス名，ループ有無，バッファ長，サンプリングレート）-> AudioClip を取得
        _recordingClip = Microphone.Start(_usedDeviceName, false, maxRecordLengthSec, sampleRate);
        _isRecording = true;
    }

    // ---------- Public Methods ----------

    // マイク録音停止して，その音声を /api/stt に送る
    public void StopRecordingAndSend()
    {
        // 録音中でなければ何もしない
        if (!_isRecording)
            return;

        _isRecording = false;

        // 録音クリップが null なら警告を出して終了
        if (_recordingClip == null)
        {
            Debug.LogWarning("[UtopiaSttClient] StopRecording called but _recordingClip is null.");
            return;
        }

        // 録音停止：現在の録音位置(バッファのどこまでかのサンプル数)を取得してから停止する
        int samplePosition = Microphone.GetPosition(_usedDeviceName);
        Microphone.End(_usedDeviceName);

        // デバッグログを出力
        if (logDebug)
        {
            Debug.Log($"[UtopiaSttClient] StopRecording pos={samplePosition} / " +
                      $"{_recordingClip.samples} samples");
        }

        //  録音位置が0以下（一瞬で離すなど）なら警告を出して終了
        if (samplePosition <= 0)
        {
            Debug.LogWarning("[UtopiaSttClient] No audio captured (samplePosition <= 0).");
            return;
        }

        
        int channels = _recordingClip.channels;
        int totalSamples = _recordingClip.samples * channels;

        // // AudioClipの中身（波形データ）をfloat配列としてすべて取り出す
        var allData = new float[totalSamples];
        _recordingClip.GetData(allData, 0);

        // トリミング：sanmplePositonの位置までのデータだけを抜き出す
        int recordedSamples = Mathf.Min(samplePosition * channels, totalSamples);
        var trimmed = new float[recordedSamples];
        Array.Copy(allData, trimmed, recordedSamples);

        if (logDebug)
        {
            float durationSec = (float)samplePosition / sampleRate;
            Debug.Log($"[UtopiaSttClient] Recorded {recordedSamples} samples, " +
                      $"channels={channels}, duration~={durationSec:F2}s");
        }

        // float配列の音声データ → WAV形式のバイナリデータに変換（WavUtility：自作クラス）
        byte[] wavBytes = WavUtility.FromAudioData(trimmed, sampleRate, channels);

        StartCoroutine(SendSttAndAskTts(wavBytes));
    }

    // ---------- Coroutines ----------

    // /api/stt に WAV を投げ，text を受け取って UtopiaClient に渡す
    private IEnumerator SendSttAndAskTts(byte[] wavBytes)
    {
        var url = $"{baseUrl}/api/stt";

        using var req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(wavBytes); // wavのバイト配列をそのままボディに乗せる
        req.downloadHandler = new DownloadHandlerBuffer(); // レスポンスを全部メモリ上に保持するハンドラ
        req.SetRequestHeader("Content-Type", "audio/wav"); // WAVデータであることを指定
        req.timeout = 120; // タイムアウト120秒

        if (logDebug)
        {
            Debug.Log($"[UtopiaSttClient] POST {url} ({wavBytes.Length} bytes)");
        }

        // サーバーから応答が来るまで待機
        yield return req.SendWebRequest();


        // 通信エラー処理
        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[UtopiaSttClient] STT request failed: {req.error}");
            yield break;
        }

        // レスポンスのJSONを取得し，冒頭に定義したSttResponseクラスのインスタンスに変換
        string json = req.downloadHandler.text;
        if (logDebug)
        {
            Debug.Log($"[UtopiaSttClient] STT response JSON: {json}");
        }

        SttResponse resp = null;
        try
        {
            resp = JsonUtility.FromJson<SttResponse>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[UtopiaSttClient] Failed to parse STT JSON: {e.Message}");
        }

        // textフィールドが空なら警告を出して終了
        if (resp == null || string.IsNullOrWhiteSpace(resp.text))
        {
            Debug.LogWarning("[UtopiaSttClient] STT result is empty.");
            yield break;
        }

        // 中身がある場合，認識されたテキストをログ出力
        Debug.Log($"[UtopiaSttClient] STT text: {resp.text}");

        // （パイプライン結合点）既存の UtopiaClient に認識されたテキスト(rest.text)を渡して /api/ask_tts を叩く
        if (utopiaClient != null)
        {
            // UtopiaClient にテキストをセットして再生開始
            utopiaClient.SetPrompt(resp.text);
            // LLMとTTS再生のパイプラインを開始
            utopiaClient.PlayCurrentPrompt();
        }
        else
        {
            Debug.LogWarning("[UtopiaSttClient] utopiaClient is not set. Only logging text above.");
        }
    }

}
　