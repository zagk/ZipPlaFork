# ZipPla雑改造版

## これはなに？
`https://sites.google.com/site/riostoolbox/zippla`（リンク切れ）で配布されていたZipPlaの雑改造版です。  
オリジナルのreadmeは[readme_original.txt](readme_original.txt)を参照。

## 改造内容
### サムネイル読み込み処理の改善
現在表示しているアイテムのみサムネイルを読み込むようにしました。  
これにより、ファイル数の多いフォルダを開いた場合でもメモリ使用量が増加しなくなります。

## ビルドに関して
Releaseビルドの場合サムネイルの読み込み失敗時にエラーダイアログが表示されます。  
AutoBuildビルドの場合はエラーダイアログが表示されないため、こちらを利用を推奨します。

### d1 での変更
- ターゲットフレームワークを .NET Framework 4.5.2 から **4.8** に変更しました。
  4.5.2 のターゲティングパックは配布終了のため、そのままではビルドできません。
- 古い `PostBuildEvent`（`Properties\PostprocessorForPerformance.js` を cscript で実行する処理）を削除しました。
  この処理は `*.Designer.cs.bak` があれば現在の Designer ファイルを上書きし、最新の Windows では
  `.js` スクリプトエンジンが無いためビルド自体が失敗していました。
- `ZipPla.exe -selftest` でパーサーなどの自己検証を実行できます（終了コード 0 = 全通過）。
- 詳細は [ZIPPLA_FORK_D1_CHANGELOG.md](ZIPPLA_FORK_D1_CHANGELOG.md) を参照してください。

### d2 での変更
- **サムネイル生成の高速化**。サムネイル読み込みが `static SemaphoreSlim(1, 1)` でプロセス全体
  直列化されていたため、コア数に関係なく常に 1 枚ずつしかデコードされませんでした。
  メモリ量を基準にした同時実行制限（`ThumbnailLimiter.cs`）に置き換えました。
  - 実測（25MB 前後の AI 生成 PNG 7 枚 / 16 コア）: 1,540ms → **400ms**（3.8倍）
  - JPEC 6000x4000 1 枚: 84.6ms → **24.7ms**（3.4倍、WIC の縮小デコード）
- 縮小デコードは焦点点を使わない `Letterbox` / `PanAndScan` の切り抜きモードでのみ使用します。
  `PlaClip`（既定）は焦点点がソース解像度に敏感で、切り抜き位置が変わってしまうためです。
- `ZipPla.exe -benchthumb "フォルダ"` でサムネイル生成速度を計測できます。
- 詳細は [ZIPPLA_FORK_D2_CHANGELOG.md](ZIPPLA_FORK_D2_CHANGELOG.md) を参照してください。

## ライセンス
オリジナルのZipPlaと同じく、AGPLでライセンスされています。