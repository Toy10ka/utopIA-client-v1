using System;
using System.Collections; // コルーチン
using System.Collections.Generic;
using System.Text; // 文字列のエンコード
using UnityEngine;
using UnityEngine.Networking; // HTTP


public class UtopiaClient : MonoBehaviour
{

    #region public property

    #endregion

    [Header("utopia-server")] // inspectorに表示されるグループ見出し
    [Tooltip("ex. http://localhost:5000 (quest: http://<PCのIP>:5000)")] // マウスを乗せたら出るヒント
    [SerializeField] private string baseUrl = "http://192.168.0.79:5000";

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource; // 再生機

    [Range(0, 10)] public int voicevoxSpeaker = 1; // VOICEVOXの話者ID指定（スライダーで）

    [Header("prompt")]
    [TextArea(2, 5)] public string promptInInspector = "自己紹介を一文で。";

    // 追加: 実際に送る“現在プロンプト”（音声認識などで後から上書き可）
    private string _currentPrompt;

    #region private method

    #endregion

    // マジックメソッド：GameObjectにスクリプトを追加したとき
    private void Reset()
    {
        // コンポ取得
        audioSource = GetComponent<AudioSource>();
        // 無ければ自動追加
        if (!audioSource) audioSource = gameObject.AddComponent<AudioSource>();
        // Unity起動時に勝手に音を再生しない（スクリプトで制御）
        audioSource.playOnAwake = false;

        // 追加（または Awake で同様の1行を追加）
        _currentPrompt = promptInInspector;
    }

    // ---------- Public API ----------

    //　API通信コルーチン関数：/api/ask_text
    public IEnumerator AskText(string prompt, Action<string> onDone, Action<string> onError = null)
    {
        // アクセスするAPIのURLを組み立てる
        var url = $"{baseUrl}/api/ask_text";

        // 送信するjsonを作る
        var body = $"{{\"prompt\":\"{JsonEscape(prompt)}\"}}"; // JsonEscape() :文字列中の " や \n などを安全にエスケープ

        // HTTPリクエスト作成：（Python の requests）
        using var req = new UnityWebRequest(url, "POST"); // using: req使ったら自動で破棄

        // POSTの中身設定：body のJSON文字列をUTF-8バイト配列に変換.送信データに設定
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));

        // サーバーからのレスポンスを全部メモリ上に保持するハンドラ
        req.downloadHandler = new DownloadHandlerBuffer();

        // HTTPヘッダー設定：
        req.SetRequestHeader("Content-Type", "application/json; charset=utf-8");

        // 通信タイムアウト設定
        req.timeout = 60;

        // 実際にリクエストを送信し、結果が返るまで待機。
        // コルーチンなのでメインスレッドをブロックしない
        yield return req.SendWebRequest();


        // 通信に失敗した場合：onError呼び出し
        if (req.result != UnityWebRequest.Result.Success)
        {
            // デリゲート呼び出し
            onError?.Invoke(req.error);
            yield break;
        }

        // ★ 成功時にログを出す
        Debug.Log($"[UtopiaClient] Response received from {baseUrl}: {req.downloadHandler.text}");
        onDone?.Invoke(req.downloadHandler.text);

        try
        {
            // {"text": ""}を取得するだけ
            // レスポンスを文字列として取得
            var json = req.downloadHandler.text;

            // JSON 文字列から `"text"` というキーの値を取り出す（自作関数）
            var text = SimpleJsonGet(json, "text");

            // 成功したら結果の文字列を渡す
            onDone?.Invoke(text);
        }
        catch (Exception e) // `Exception e` ：発生した例外オブジェクト
        {
            // onErrorにエラーメッセージを渡す
            onError?.Invoke(e.Message);
        }
    }

    //　API通信コルーチン関数：/api/ask_tts
    public IEnumerator AskTTS(string prompt, int? speakerOverride = null, // 話者上書き（nullなら無し）
                              Action<AudioClip> onDone = null, Action<string> onError = null)
    {
        var url = $"{baseUrl}/api/ask_tts";
        var speaker = speakerOverride ?? voicevoxSpeaker;
        var body = $"{{\"prompt\":\"{JsonEscape(prompt)}\",\"speaker\":{speaker}}}";

        using var req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
        req.timeout = 180;

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            onError?.Invoke(req.error);
            yield break;
        }

        try
        {
            var bytes = req.downloadHandler.data;
            if (bytes == null || bytes.Length < 44) { onError?.Invoke("WAV応答が空/不正"); yield break; }

            // 16bit PCM WAV を AudioClip に
            var clip = WavUtility.ToAudioClip(bytes, "utopia_tts");
            if (!clip) { onError?.Invoke("WAVデコード失敗"); yield break; }

            if (audioSource)
            {
                audioSource.Stop();
                audioSource.clip = clip;
                audioSource.Play();
            }

            onDone?.Invoke(clip);
        }
        catch (Exception e)
        {
            onError?.Invoke(e.Message);
        }
    }
    // ---------- Helper ----------

    // C# の文字列を JSON に埋め込むときに、JSONとして不正にならないようエスケープ
    static string JsonEscape(string s)
    {
        // 空文字または null のときは空文字を返して安全に終了
        if (string.IsNullOrEmpty(s)) return "";
        // バックスラッシュ，ダブルクォート，改行を置換
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }


    // JSON文字列を翻訳してkeyを取り出す
    static string SimpleJsonGet(string json, string key)
    {
        //  {"text":"..."} 用の簡単なキー抽出
        // keyが"text"なら、tagには""text":"という文字列
        var tag = $"\"{key}\":";
        // tagを探す
        var i = json.IndexOf(tag, StringComparison.Ordinal);
        if (i < 0) return "";
        // iが"text":の直後(値が始まる位置)を指す
        i += tag.Length;
        // 先頭のクオートへ（値の開始を告げる"が見つかるまでiを1つずつ進めてスペースを読み飛ばす）
        while (i < json.Length && json[i] != '"') i++;
        // "がなければ空文字列
        if (i >= json.Length) return "";
        // 中身の文字へ
        i++;
        var j = i;
        // 高速に文字列を組み立てるクラス
        var sb = new StringBuilder();
        // 値終わりの”が来るまでループ
        while (j < json.Length && json[j] != '"')
        {
            // \を検知
            if (json[j] == '\\' && j + 1 < json.Length)
            {
                j++;
                char c = json[j] switch
                {
                    // 翻訳
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => json[j]
                };
                // 翻訳語を追加
                sb.Append(c);
            }
            else sb.Append(json[j]);
            j++;
        }
        // 最終的な文字列
        return sb.ToString();
    }

    // 追加: 将来的に音声認識などから呼んでプロンプトを上書きする
    public void SetPrompt(string text)
    {
        _currentPrompt = string.IsNullOrWhiteSpace(text) ? promptInInspector : text;
    }

    // 追加: 現在のプロンプトで /api/ask_tts を実行
    public void PlayCurrentPrompt()
    {
        var text = string.IsNullOrWhiteSpace(_currentPrompt) ? promptInInspector : _currentPrompt;
        // StartCoroutine(AskTTS(text));
        StartCoroutine(AskTTS(
            text,
            null,
            // 成功時の処理 (onDone)
            (clip) => {
                // サーバー通信成功のログ
                Debug.Log($"[UtopiaClient] AskTTS Success. AudioClip loaded and played.");
            },
            // 失敗時の処理 (onError)
            (errorMsg) => {
                // サーバー通信失敗のログ（赤色で表示）
                Debug.LogError($"[UtopiaClient] AskTTS Error: {errorMsg}");
            }
        ));
    }







    // Unityがシーンを開始した時に自動で呼ばれる関数
    void Start()
    {
        // 5秒後に自動テストを開始するコルーチンを起動します
        StartCoroutine(AutomaticServerTest());
    }

    // 自動テスト用のコルーチン（並行処理）
    IEnumerator AutomaticServerTest()
    {
        // 5秒間待機します
        // (アプリの起動やWi-Fi接続が安定するのを待つため)
        Debug.Log("[UtopiaClient] 5秒後に自動サーバーテストを実行します...");
        yield return new WaitForSeconds(5.0f);

        // 5秒経過したら、コンソールにログを出し、
        // Aボタンが押された時と「全く同じ」処理を呼び出します。
        // これがVRアプリ内からサーバーを呼び出すテストになります。
        Debug.Log("[UtopiaClient] ★自動テスト★ サーバー呼び出し (PlayCurrentPrompt) を実行します...");
        PlayCurrentPrompt();
    }

    // --- ここまで ---










    // ---------- Space または touchコントローラーAボタンで動作確認 ----------
    //void Update()
    //{
    //    if (Input.GetKeyDown(KeyCode.Space) || InputManagerLR.PrimaryButtonR_OnPress())
    //    {
    //        Debug.Log("[UtopiaClient] Input detected -> PlayCurrentPrompt()");
    //        PlayCurrentPrompt();
    //    }
    //}

    void Update()
    {
        // --- 1. Spaceキーの検知 ---
        if (Input.GetKeyDown(KeyCode.Space))
        {
            Debug.Log("[UtopiaClient] Space key pressed. Calling PlayCurrentPrompt()...");
            PlayCurrentPrompt();
        }

        // --- 2. VRのAボタンの検知 ---
        if (InputManagerLR.SecondaryButtonR_OnPress())
        {
            // ログ: Aボタンが押されたことを記録
            Debug.Log("[UtopiaClient] VR SecondaryButtonR (B Button) pressed. Calling PlayCurrentPrompt()...");
            PlayCurrentPrompt();
        }
    }

}


