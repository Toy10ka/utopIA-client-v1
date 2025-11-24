// Input Action のボタンのマネージャーを作成

using UnityEngine;
using UnityEngine.InputSystem; // Unityの新しいInput Systemを使うために必要

public class InputManagerLR : MonoBehaviour
{
    // --- シングルトン化のための準備 ---
    static InputManagerLR m_instance;

    // --- Inspectorで設定する項目 ---
    [SerializeField]
    InputActionAsset m_actionAsset; // ここに .inputactions ファイルが入る

    // --- スクリプト内部で使う変数 ---
    InputActionMap m_ActionMap;     // "Interaction" アクションマップを保持する変数
    InputAction m_PrimaryButtonR;  // "XR_PrimaryButtonR" (Aボタン) のアクションを保持
    InputAction m_PrimaryButtonL;  // "XR_PrimaryButtonL" (Xボタン) のアクションを保持
    InputAction m_SecondaryButtonR; // "XR_SecondaryButtonR" (Bボタン) のアクションを保持
    InputAction m_SecondaryButtonL; // "XR_SecondaryButtonL" (Yボタン) のアクションを保持

    // --- 起動時に1回だけ呼ばれる処理 ---
    private void Awake()
    {
        // m_instance に，このスクリプト（コンポーネント）自身の実体を代入
        // 他のスクリプトが 'm_instance' を通じてこのスクリプトの機能を使えるようになる
        m_instance = this;

        // このマネージャーがアタッチされているGameObjectを，シーンが切り替わっても破棄しない
        // どのシーンからでも入力管理ができる
        GameObject.DontDestroyOnLoad(gameObject);

        // m_actionAsset（.inputactionsファイル）から，指定したアクションマップ（"Interaction"）を見つけ出す
        m_ActionMap = m_actionAsset.FindActionMap("Interaction");

        // アクションマップの中から，各アクション（ボタン）を名前で探し出す
        // 'throwIfNotFound: true' は，見つからなかった場合にエラーを出す設定
        m_PrimaryButtonR = m_ActionMap.FindAction("XR_PrimaryButtonR", throwIfNotFound: true);
        m_PrimaryButtonL = m_ActionMap.FindAction("XR_PrimaryButtonL", throwIfNotFound: true);
        m_SecondaryButtonR = m_ActionMap.FindAction("XR_SecondaryButtonR", throwIfNotFound: true);
        m_SecondaryButtonL = m_ActionMap.FindAction("XR_SecondaryButtonL", throwIfNotFound: true);
    }

    // --- オブジェクトが有効になった時に呼ばれる処理 ---
    private void OnEnable()
    {
        // "Interaction" アクションマップ全体を有効化
        // これをしないとボタンを押しても反応しない
        // '?' ：m_ActionMapのnullチェック
        m_ActionMap?.Enable();
    }

    // --- オブジェクトが無効になった時に呼ばれる処理 ---
    private void OnDisable()
    {
        // アクションマップを無効化
        // オブジェクトが非アクティブな時に無駄に入力を監視するのを防ぐ
        m_ActionMap?.Disable();
    }

    // --- 他のスクリプトから呼び出すための関数 ---

    // [Aボタン] が「押されている間」ずっと true を返す関数
    public static bool PrimaryButtonR()
    {
        // IsPressed() : 押されている間 true
        return m_instance.m_PrimaryButtonR.IsPressed();
    }

    // [Aボタン] が「押された瞬間」に1フレームだけ true を返す関数
    public static bool PrimaryButtonR_OnPress()
    {
        // WasPressedThisFrame() : 押された瞬間のフレームだけ true
        return m_instance.m_PrimaryButtonR.WasPressedThisFrame();
    }

    // [Aボタン] が「離された瞬間」に1フレームだけ true を返す関数
    public static bool PrimaryButtonR_OnRelease()
    {
        // WasReleasedThisFrame() : 離された瞬間のフレームだけ true
        return m_instance.m_PrimaryButtonR.WasReleasedThisFrame();
    }


    // --- [Xボタン] Left Primary ---

    public static bool PrimaryButtonL()
    {
        return m_instance.m_PrimaryButtonL.IsPressed();
    }

    public static bool PrimaryButtonL_OnPress()
    {
        return m_instance.m_PrimaryButtonL.WasPressedThisFrame();
    }

    public static bool PrimaryButtonL_OnRelease()
    {
        return m_instance.m_PrimaryButtonL.WasReleasedThisFrame();
    }

    // --- [Bボタン] Right Secondary ---

    public static bool SecondaryButtonR()
    {
        return m_instance.m_SecondaryButtonR.IsPressed();
    }

    public static bool SecondaryButtonR_OnPress()
    {
        return m_instance.m_SecondaryButtonR.WasPressedThisFrame();
    }

    public static bool SecondaryButtonR_OnRelease()
    {
        return m_instance.m_SecondaryButtonR.WasReleasedThisFrame();
    }

    // --- [Yボタン] Left Secondary ---

    public static bool SecondaryButtonL()
    {
        return m_instance.m_SecondaryButtonL.IsPressed();
    }

    public static bool SecondaryButtonL_OnPress()
    {
        return m_instance.m_SecondaryButtonL.WasPressedThisFrame();
    }

    public static bool SecondaryButtonL_OnRelease()
    {
        return m_instance.m_SecondaryButtonL.WasReleasedThisFrame();
    }
}
