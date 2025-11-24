using System.Collections; // コルーチン
using System.Collections.Generic; //List, Queueとか
using UnityEngine;

using System; // String, Action, Consoleなど多岐
using System.IO; // ファイルストリーム
using System.Text; // エンコードや文字列処理

// GameObjectに依存しないただの関数
  

// インスタンスを作れない（newできない）クラス(ただの関数置き場, 静的メンバしか置けない)
public static class WavUtility
{
    // wav(byte配列)をAudioClip(音声オブジェクト)に変換
    public static AudioClip ToAudioClip(byte[] wavData, string name = "wav")
    {
        // byte[]をストリーム化: スコープ抜けたら破棄
        using var ms = new MemoryStream(wavData);
        // ストリームをバイナリ単位で読み取る
        using var br = new BinaryReader(ms);


        // WAV の先頭は必ず "RIFF" ,ファイル全体サイズ , "WAVE" という構造

        // "RIFF": 先頭4バイト確認
        var riff = Encoding.ASCII.GetString(br.ReadBytes(4));
        if (riff != "RIFF") return null;

        // ファイル全体サイズ（ヘッダー分を除く）: 今回は使わないので捨てる
        br.ReadInt32(); // file size - 8

        // WAVE: 次の4バイトを確認
        var wave = Encoding.ASCII.GetString(br.ReadBytes(4));
        if (wave != "WAVE") return null;

        // fmt チャンク解析（音声フォーマット情報）
        // "fmt " チャンクは音声データの仕様情報。 "fmt " でなければ不正なWAV
        var fmt = Encoding.ASCII.GetString(br.ReadBytes(4));
        if (fmt != "fmt ") return null;

        var fmtSize = br.ReadInt32();
        var audioFormat = br.ReadInt16();      // 1=PCM
        var numChannels = br.ReadInt16();      // 1 or 2
        var sampleRate = br.ReadInt32();
        br.ReadInt32();                        // byte rate
        br.ReadInt16();                        // block align
        var bitsPerSample = br.ReadInt16();    // 16 expected
        // WAV拡張仕様では fmtチャンクの後に余分な情報がある場合が。その分をスキップして次のチャンクへ進む
        if (fmtSize > 16) br.ReadBytes(fmtSize - 16); // skip extra


        // data チャンクを探す（実際の音声データ）
        // WAVは "fmt " の次にすぐ "data" が来るとは限らないので，"data" チャンクを見つけるまで繰り返す
        string chunkId;
        int dataSize = 0;
        do
        {
            chunkId = Encoding.ASCII.GetString(br.ReadBytes(4));
            dataSize = br.ReadInt32();
            if (chunkId != "data") br.ReadBytes(dataSize);
        } while (chunkId != "data");

        // "data" チャンクの実際のPCMデータを読み取る。
        // dataSize バイト分が音の本体データ
        var bytes = br.ReadBytes(dataSize);


        // WAVの16bitPCM (-32768〜32767) → AudioClipが扱う範囲(float[-1..1]) への変換
        // 2バイトで1サンプル（16bit）なので, サンプル総数＝音声全体のデータサイズ/2
        int totalSamples = dataSize / 2;
        // ステレオ（2ch）などの場合、1チャンネルあたりのサンプル数を出す
        int sampleCount = totalSamples / numChannels;
        // Unity用の配列を確保. すべてのチャンネルのサンプル（交互に並ぶ）を格納(インターリーブ（interleave）)
        var samples = new float[totalSamples];

        int offset = 0;
        for (int i = 0; i < totalSamples; i++)
        {
            // bytes の2バイトずつを short（符号付き16bit整数）として読み取る
            short s = BitConverter.ToInt16(bytes, offset);
            // -32768〜32767 → -1.0〜1.0 に正規化して samples に格納
            samples[i] = s / 32768f;
            // offset を2ずつ進めてすべてのサンプルを読む
            offset += 2;
        }

        // AudioClip を生成
        // (名前, サンプル数, チャンネル数, サンプルレート, ストリーミング再生かどうか)
        var clip = AudioClip.Create(name, sampleCount, numChannels, sampleRate, false);

        // モノラル
        if (numChannels == 1)
        {
            // SetData() に float[] を渡して波形データをセット
            clip.SetData(samples, 0);
        }
        else
        {
            // 条件分岐しているが実際は両方同じ処理（将来拡張用）
            // Unityはインターリーブ（L, R, L, R, ...）で SetData を受け取る仕様なのでそのまま渡せる
            clip.SetData(samples, 0);
        }

        // 生成したAudioClipを返す
        return clip;
    }

    // float[] 型の音声データを，WAVフォーマット（16bit PCM）のバイト配列に変換
    public static byte[] FromAudioData(float[] samples, int sampleRate, int channels = 1)
    {
        // samples: 音声波形データ（-1.0f 〜 1.0f の数値の羅列）
        // sampleRate: 1秒間のサンプル数（例: 44100, 16000）
        // channels: チャンネル数（1ならモノラル、2ならステレオ）
        // 戻り値: WAVファイルのバイナリデータ（byte[]）

        // 引数チェック
        if (samples == null || samples.Length == 0)
            throw new ArgumentException("samples is null or empty", nameof(samples));
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "channels must be >= 1");

        // WAVヘッダー情報の計算 (標準は44バイト)
        const int headerSize = 44; 
        // 音質16bit (CD音質)
        short bitsPerSample = 16; 
        int sampleCount = samples.Length;
        // 総サンプル数 (1サンプル当たり16bit = 2byte)
        int byteCount = sampleCount * (bitsPerSample / 8); 

        // 最終的に返すバイト配列を確保（ヘッダ44 + 音声データ中身）
        byte[] bytes = new byte[headerSize + byteCount];

        // WAVヘッダー情報の計算
        int sampleRateLocal = sampleRate;
        short channelsShort = (short)channels;
        int byteRate = sampleRateLocal * channelsShort * bitsPerSample / 8;
        short blockAlign = (short)(channelsShort * bitsPerSample / 8);
        int subchunk2Size = byteCount;
        int chunkSize = 36 + subchunk2Size;

        // ---- RIFF ヘッダ ----
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BitConverter.GetBytes(chunkSize).CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);

        // ---- fmt チャンク ----
        Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12);
        BitConverter.GetBytes(16).CopyTo(bytes, 16);             // Subchunk1Size (PCM)
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);       // AudioFormat = 1 (PCM)
        BitConverter.GetBytes(channelsShort).CopyTo(bytes, 22);  // NumChannels
        BitConverter.GetBytes(sampleRateLocal).CopyTo(bytes, 24);// SampleRate
        BitConverter.GetBytes(byteRate).CopyTo(bytes, 28);       // ByteRate
        BitConverter.GetBytes(blockAlign).CopyTo(bytes, 32);     // BlockAlign
        BitConverter.GetBytes(bitsPerSample).CopyTo(bytes, 34);  // BitsPerSample

        // ---- data チャンク ----
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36); // ここからが音声データ
        BitConverter.GetBytes(subchunk2Size).CopyTo(bytes, 40); // これから続く音声データの総バイト数．ここまででヘッダ44バイト

        // 音声データの変換と書き込み
        // 書き込み開始位置を44バイト目（ヘッダ直後）にセット
        int offset = headerSize;

        // 全サンプル数だけループ
        for (int i = 0; i < sampleCount; i++)
        {
            // クリッピング
            float sample = Mathf.Clamp(samples[i], -1f, 1f);
            // float(-1.0 ~ 1.0)を量子化（16bit化）
            short intSample = (short)Mathf.RoundToInt(sample * short.MaxValue);
            // バイト配列に変換して書き込み
            BitConverter.GetBytes(intSample).CopyTo(bytes, offset);
            // 次のサンプルの書き込み位置へ移動
            offset += 2;
        }

        // 完成したWAVバイト配列を返す
        return bytes;
    }


}