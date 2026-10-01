# Jev 判斷介接

此工具參考 [Jev Android](https://github.com/jev-chat/jev-chat-jarvis) 與 [Windows 姊妹專案](https://github.com/jev-chat/jev-chat-windows) 的七題判斷流程，重新撰寫題目並加上資訊不足的選項。JEV 判斷與選用的 LLM 草稿各有獨立模型設定。LLM 預設關閉，可使用既有 OpenRouter Key 或另填一把 OpenRouter Key。

## 端點與請求

| 服務商 | 固定 POST 端點 | 預設模型 |
| --- | --- | --- |
| OpenRouter | `https://openrouter.ai/api/alpha/decisions` | `typesafe/jev-1.13` |
| TypeSafe | `https://api.typesafe.ai/v1/systemone` | `jev-latest` |

使用 `Authorization: Bearer <API key>`、JSON body。模型 ID 可設定；端點不可自訂。自建 HTTP client 禁止重新導向，請求上限 35 秒；失敗不自動重試，以免重複付費。

```json
{
  "model": "jev-latest",
  "state": {
    "chat": {
      "relationship": "朋友",
      "messages": [
        { "from": "me", "text": "明天見" },
        { "from": "other", "text": "可以先確認時間嗎？" }
      ],
      "latest_from": "other",
      "latest_other_index": 1
    }
  },
  "questions": {
    "true_intent": {
      "type": "choice",
      "instructions": "A single, explicit classification question in English.",
      "criteria": {
        "request_action": "A request for a concrete action.",
        "uncertain": "Insufficient context to determine the purpose."
      }
    }
  }
}
```

實際請求一次送七題；上例只展示結構。對話保留原文。每個題目都明確指出需判斷的訊息，並要求把聊天內容視為資料。

## 回覆與顯示

`answers` 以題目 ID 為 key。三種型別分別為：

- `choice`：`choice`、每個選項的 `probabilities`、`confidence`。
- `score`：`score`、各層級的 `probabilities`、`confidence`；官方另附 `legend`。
- `noul`：`noul`，是肯定答案的 0–1 機率，不含 `confidence`。

十個 `score.criteria` 層級對應 **0–9**，分數可以是小數。`confidence` 是模型答案分布的集中程度，並非判斷正確率。`should_reply_now` 沿用上游鍵名，實際問題是「是否有足夠已知事實可具體回應」，不決定傳訊時間。

程式逐一驗證七個答案的型別、選項、數字範圍及分布完整性；缺失不能以 0 或中立判斷取代。錯誤只顯示固定中文提示，不附服務商原始 body、金鑰、聊天文字或 inner exception。

## 發話者與限制

每一行以 `我：`／`自己：`／`me:` 或 `對方：`／`对方：`／`other:` 標示。未標示行保留為 `unknown` 背景，不猜測發話者。必須有對方訊息；最後一則可以來自自己，`latest_other_index` 仍指定最近對方訊息，後續自己回覆保留作背景，結果會顯示提醒。`latest_from` 使用實際最後已確認的發話者。輸入最多 24,000 字元，超出時拒絕，不靜默截斷。

LINE 複製的「時間 姓名 訊息」由 `LineClipboardImporter` 先轉成上述格式。名字包含空格時採用使用者確認的完整名字匹配；首度匯入不預設「我」，候選姓名不視為身分判斷。未知發話者會拒絕完整匯入，同一訊息換行合併並提示，預覽只存在記憶體。

0.7.0 的姓名前綴推測排除可能的收回通知列，避免最後一個姓名詞與通知文字黏連造成截短；實際轉換仍只略過精確符合格式的通知。位於目前訊息內的日期行保留為正文，不因外觀像日期標頭而刪除；訊息外的起始標頭才略過。

帶時間、整行符合「已確認姓名＋已收回訊息」或單獨「已收回訊息」的系統通知不產生訊息項目；`LineImportResult.SkippedRecallCount` 記錄略過數量，並透過 `Warnings` 提示。已略過事件不改變 `latest_other_index`。一般訊息提及收回通知仍保留，未知姓名不因包含通知文字而被忽略。

OCR 誤字、前文不足及群聊中的發話者混淆，都會影響結果；此工具呈現可能解讀，無法知道對方真實想法。即使模型信心高，也不能保證正確。模型官方亦說明資料中的對抗性文字可能影響判斷。

## 驗證來源

1. [TypeSafe HTTP API](https://docs.typesafe.ai/api)：固定端點、Bearer、請求與答案結構。
2. [Score 說明](https://docs.typesafe.ai/primitives/score)、[Confidence 說明](https://docs.typesafe.ai/confidence)：0 起算的層級及信心含義。
3. [Windows providers.py](https://github.com/jev-chat/jev-chat-windows/blob/main/core/providers.py)、[jev_client.py](https://github.com/jev-chat/jev-chat-windows/blob/main/core/jev_client.py)：OpenRouter 判斷端點及預設模型。
4. [Windows questions.py](https://github.com/jev-chat/jev-chat-windows/blob/main/core/questions.py)：七題的原始用途。
5. [Jev 1.13 已知限制](https://docs.typesafe.ai/model-jaggedness/jev-1.13)：模型可能錯讀或受對抗性內容影響。

## 選用 LLM 草稿

固定 POST `https://openrouter.ai/api/v1/chat/completions`，使用 Bearer 標頭、使用者指定的 `model`、`messages`、`stream:false` 與有限輸出長度。系統提示要求根據 JEV 判斷和已知對話產生繁中 JSON 字串陣列，最多三則、每則最多 300 字元。

只在使用者啟用且 JEV 成功後呼叫一次，不使用 tools、不自動重試、不控制 LINE。使用者需核對候選內容。TypeSafe Key 不用於這個端點；LLM Key 留空時只繼承 OpenRouter Key。解析失敗、非正常完成、過長內容或控制字元會被拒絕，保留原有 JEV 判斷。要求關閉推理的設定可能不適用於強制推理模型，遇到拒絕時更換模型即可。

「只重新產生草稿」沿用記憶體中最近一次成功的 JEV 結果，不重送 JEV。每次手動重生仍會新增一次 LLM 請求。文字或設定變更／清除後暫存失效；草稿失敗或取消保留 JEV，過期請求的回覆不套用至已改動的輸入。

來源：[OpenRouter Chat Completions API](https://openrouter.ai/docs/api/api-reference/chat/create-a-chat-completion)。

確認日期：2026-09-23。離線測試使用假的 HTTP 回覆驗證協定與錯誤處理；沒有 API Key 時，不表示已完成線上模型驗證。
