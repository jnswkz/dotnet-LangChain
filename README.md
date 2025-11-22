# dotnet-LangChain チャットボット

## 概要
- .NET 9 で動く RAG チャットボット。PostgreSQL のメタデータと PDF テキストをベクトル化し、Gemini 2.5 Pro へコンテキストとして渡します。
- pgvector 拡張を使い、`kb_docs` テーブルに埋め込みを保存します。
- 回答はベトナム語で、コンテキストの出典を付けて返します。

## 必要なもの
- .NET 9 SDK
- PostgreSQL + pgvector 拡張が有効な DB
- 環境変数: `GOOGLE_API_KEY`, `AZURE_POSTGRES_URL`
- PDF を置くフォルダ: `./pdfs/` (存在しない場合は作成してください)

## セットアップ
1) `.env` にキーを設定  
```
GOOGLE_API_KEY=your_gemini_key
AZURE_POSTGRES_URL=postgres://user:pass@host:port/db
```
2) 依存関係は `dotnet restore` で取得  
3) `dotnet run` で起動

## 仕組み
- `Program.cs`  
  - `.env` 読み込み、PDF のプレーンテキストを確認後、Gemini と Postgres 接続を初期化し、ベクトル同期と Q&A ループを実行
- `data.cs`  
  - `SyncVectorStoreAsync`: DB メタデータをドキュメント化し埋め込み→`kb_docs` に upsert  
  - `SimilaritySearchAsync`: クエリ埋め込みとの距離で上位コンテキストを取得
- `embbeding.cs`  
  - Google text-embedding-004 で単発/バッチ埋め込みを生成し正規化
- `pdf.cs`  
  - `ReadPdfFile`: `./pdfs` から PDF リストを取得  
  - `GetPlainText`: PdfPig で PDF テキスト抽出（スキャン PDF は別途 OCR が必要）

## PDF 取り込みの流れ
1) `./pdfs` に PDF を配置  
2) `GetPlainText` でテキスト抽出（抽出できない場合は OCR でテキスト化してから利用）  
3) テキストをチャンク分割し、`EmbedAsyncBatch` → `UpsertDocsAsync` で `kb_docs` に保存  
4) 質問時に PDF 由来のチャンクも検索対象となり、コンテキストとして LLM に渡されます

## 実行と確認
- `dotnet run` 実行後、プロンプトに質問を入力すると、DB と PDF のコンテキストを用いた回答が返ります。
- ベクトルテーブルや PDF を変えた場合は再度 `dotnet run` して再インデックスしてください。

## 注意
- スキャン PDF は `GetPlainText` では抽出できません。OCR でテキスト化してから `./pdfs` に置いてください。
- コンテキストに取り込む情報に秘密情報が含まれないようご注意ください。
