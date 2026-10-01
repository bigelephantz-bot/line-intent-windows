# 密鑰與開源發布

API Key 在工具的設定視窗輸入。請勿將密鑰寫入原始碼、README、`.env.example`、議題、PR、測試截圖或 GitHub Actions 變數。

## 程式內的保護

- JEV／OpenRouter／選用 LLM Key 使用 Windows DPAPI 使用者範圍加密，保存在專案外的 `%LOCALAPPDATA%\LineIntent\settings.json`。發布或複製專案不會帶出這個設定檔。
- 介面預設遮罩密鑰；設定類型的 JSON 序列化與字串表示不包含密鑰。HTTP API Key 只放在所選服務的 `Authorization` 標頭。
- JEV 固定使用 OpenRouter 或 TypeSafe 端點；選用 LLM 固定使用 OpenRouter，禁止 HTTP 重新導向，不接受任意第三方 URL。
- 不記錄請求、回覆原文、聊天內容或密鑰。錯誤使用固定提示。診斷畫面只用合成資料，停用真實 LINE 視窗標題載入。
- LINE 格式匯入的原文、姓名與預覽均只留在記憶體。`--capture-diagnostics <路徑>` 是明確執行的視窗中繼資料診斷，只輸出程序、尺寸及視窗狀態，不含標題、圖片或文字辨識。
- OCR 說話者辨識、局部放大重試及核對畫面都在本機記憶體執行，圖片不傳送模型。套用只更新編輯區；按「分析」才依原設定傳送已核對的文字。
- 草稿重生的 JEV 結果只暫存在本機記憶體，輸入／設定改動或清除時失效；不另存歷史。重用的氣泡遮罩陣列在歸還緩衝池時清空。
- 若對話、關係或模型欄位包含已儲存的 Key，會停止分析，避免把密鑰誤當成模型輸入傳送。
- 發布 ZIP 僅包含編譯產物、README、LICENSE、NOTICE 與文件；不複製使用者設定、環境變數或診斷資料。

密鑰在發出 HTTPS 請求時仍必須由程式在記憶體中使用。同一 Windows 使用者下的惡意程式可以解密 DPAPI，手動顯示密鑰後的螢幕截圖也可能暴露密鑰。上述措施不能抵擋已受入侵的帳戶。

## 提交前防護

`.gitignore` 排除使用者設定、密鑰／憑證檔、環境檔、診斷資料、編譯及發布目錄。`scripts/Check-Secrets.ps1` 另外檢查常見 API Key 模式與敏感設定檔，命中時只回報路徑與規則名稱，絕不印出符合的文字。

此工作資料夾已初始化本機 Git 並啟用 hook；沒有設定遠端或推送內容。之後若重新 clone，需在新的 clone 再執行安裝腳本。從獨立原始碼副本開始時：

```powershell
git init
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-GitHooks.ps1
```

每次 `git commit` 前，hook 會檢查**暫存區的實際內容**，不能以工作目錄已刪掉密鑰取代。也可先執行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-Secrets.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-Secrets.ps1 -Staged
```

GitHub Actions 會再次執行掃描與無密鑰測試。CI 只在內容上傳後執行，**不能取代本機提交前檢查**。測試及 CI 不需要、也不應配置真實 API Key。發布前保留 hook 並執行掃描；不要用 `git add -f` 加入使用者設定或繞過 hook。

Windows UI 測試使用另外的手動 workflow，只針對預設分支、專用 runner 與受保護 environment；不以公開 PR 事件自動觸發。Runner 應是無私人資料的可重設測試 VM，不能視為安全沙箱；詳細隔離及連接條件見 [Windows UI CI](docs/windows-ui-ci.md)。目前尚未註冊遠端 runner。

掃描器只能檢出已知模式，無法保證辨識所有密鑰形式；本機 hook 也可以被跳過。主要防線是密鑰從不寫入專案，再以 ignore、hook、CI 補強。尚未推送的 Git 記錄也應視為可能日後公開，不要提交任何密鑰。

## 如果密鑰曾經出現於 Git 或公開內容

1. 立即在 OpenRouter／TypeSafe 撤銷該 Key，產生新 Key。
2. 刪除公開內容中的密鑰，確認 Git 歷史是否仍保留；改寫已分享歷史前先與協作者協調。
3. 在工具設定更新新 Key。移除檔案或改寫歷史不能讓已曝光的舊 Key 恢復安全。

回報問題時提供固定錯誤提示、版本與重現步驟。不要提供 API Key、原始設定檔、私密聊天或帶密鑰的截圖。
