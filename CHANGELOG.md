# ZipPlaFork 변경 기록 (CHANGELOG)

버전 명명 규칙: `ZipPlaFork-v17-m1`, `m2`, `m3` … (짧은 버전명, 빌드마다 뒤에 순번)
작업 폴더: `Default Project\ZipPlaFork-v17` / 산출물: `Desktop\coding\ZipPlaFork-v17-mN.zip` + 동명 실행 폴더

---

## v15 — 베이스 (ChatGPT 수정본)

원본: `ZipPlaFork-v15-layout8-no-xmp-r3-row-copy.zip`

- **layout8**: `동작 > 레이아웃` 8종. Details 4종(Thumbnails/Details 상하·좌우) +
  Metadata 4종(Thumbnails/Metadata 상하·좌우). Metadata는 독립 도킹 패널이라
  Details와 동시 표시 가능.
- **Metadata 패널**: Tiefsee4 `MainExif` 개념(Name | Value | Copy).
  PNG `tEXt`/`zTXt`/`iTXt` 직접 읽기, JPEG XMP/COM, WebP XMP 직접 읽기,
  TagLib EXIF 필드, A1111 parameters / NovelAI JSON / ComfyUI prompt JSON 파싱
  (sampler 노드 seed·steps·cfg·sampler·scheduler·model·vae·denoise·prompt·size 링크 해소).
- **no-xmp**: 원시 XMP/XML 패킷은 그리드에서 숨김 (파싱된 AI 필드만 표시).
- **r3 / row-copy**: Excel식 셀 구분선(`Single` + `ControlLight`),
  행 클릭하면 값 복사.
- **Single Window**: 명명 뮤텍스 + 네임드파이프. `-v` / `-LookAheadMode*` 뷰어 실행은
  별도 프로세스 유지. 두 번째 실행의 경로 인수는 기존 창으로 전달.
- 그 외: 탐색기 우클릭 등록 토글(폴더/.zip/.rar), 외부 경로 북마크 중복 제거.

---

## v16 — 안정화 패치 (직접 수정 + 빌드)

원본: `ZipPlaFork-v16.zip` / 실행 폴더: `ZipPlaFork-v16\`

`MetadataFeature.cs`
- PNG `zTXt`/`iTXt` **zlib 버그 수정**: zlib(RFC1950) 래퍼(2B 헤더 + Adler32)를
  벗기는 `ZlibToRaw()` 추가. 이전엔 `DeflateStream`에 그대로 넣어 표준 압축
  청크가 예외→`catch{}`로 사라졌음.
- JPEG 파서 보정: `BinaryReader` 전환, `0xFF` 패딩 스킵, 부분읽기 검사,
  청크 상한 `MaxChunkBytes=16MB`.
- WEBP 파서 보정: RIFF/WEBP 시그니처 분리 검증, 청크 상한, 길이 검증.
- 대용량 방어: 표시값 `Truncate(4000자+"…")`, JSON `MaxJsonChars=5MB` +
  `RecursionLimit=100` (`int.MaxValue`/200에서 축소).
- row-copy UX: `ClipboardCopyMode.EnableWithoutHeaderText` + 우클릭 행선택 +
  `Ctrl+C` + 컨텍스트메뉴 Copy. 기존 클릭 복사 유지.

`CatalogForm.cs`
- **레이아웃 width/height 공유 버그 수정**: `pnlMetadata_NormalWidth` 하나로
  Width/Height를 겸용해서 가로↔세로 전환 시 엉뚱한 크기가 복원됐음.
  `NormalWidth`/`NormalHeight` 분리, SizeChanged·MouseMove·setDock·ApplySettings 전부 분리 저장.

`SingleInstanceManager.cs` + `Program.cs`
- 파이프 구분자 `\n` → `\0` (경로 내 개행 깨짐 수정, 구버전 `\n` 수신 호환 유지).
- `SendToPrimary()` 실패해도 무조건 종료하던 silent-exit 수정:
  전송 성공 시에만 종료, 실패하면 정상 기동으로 폴백.

빌드 메모: `TargetFrameworkVersion=v4.8` 오버라이드로 빌드
(v4.5.2 레퍼런스 폴더에 xml만 있고 dll이 없어서. csproj 자체는 4.5.2 유지).
Release 경고 1개(`CatalogForm.cs:6690` CS0162 기존 unreachable, 무해).

---

## v16-d1 — 중간 (ChatGPT)

- 썸네일 개편 작업 중간본. selftest 50개 포함.
- 상세 diff는 `ZipPlaFork-v16-d2-changes.diff`가 d1→d2만 기록하므로,
  d1 단독 변경분은 별도 기록 없음.

## v16-d2 — 썸네일 속도 개편 (ChatGPT)

원본: `ZipPlaFork-v16-d2.zip` (상세: `ZIPPLA_FORK_D2_CHANGELOG.md`)

- **원인 1**: `ThumbViewerItem`의 전역 `static SemaphoreSlim(1,1)`이 모든 썸네일
  디코딩을 직렬화. 16코어에서도 1장씩. `bmwMakePreview.ThreadCount`는 죽은 코드였음.
- **원인 2**: 160x120 썸네일 만드는데 56MP 원본 전체 디코딩 (JPEG DCT 스케일링 미사용).
- **수정**:
  - `ThumbnailLimiter.cs` (신규): 픽셀량(픽셀×4B) 예산식 제한.
    예산 = 물리메모리 1/16 (384MB~1536MB clamp), 상한 `min(코어,16)`,
    FIFO·이중Dispose 안전·초대형 clamp(교착 방지). 미지 형식 256MB 취급.
  - `LoadAsync`: 세마포어 제거 → 제한기 lease, 대기 중 취소·경로 변경 시 결과 폐기,
    예외는 `Program.LogException` 기록. (취소-큐 문제는 v17-m1에서 해결)
  - `ImageLoader.GetScaledThumbnailSource` (신규): WIC `DecodePixelWidth/Height`
    축소 디코딩 (jpg/png/bmp). 요청 크기의 **3.5배** 밀도 유지
    (캐시 `TrySet ≥목표×2`, `TryGet ≥요청×1.414` 조건 동시 만족 → 캐시 내용 동일).
    JPEG 방향은 축소 후 적용.
  - **PlaClip(기본 잘라내기)에서는 축소 디코딩 미사용**: 초점점 계산이 소스 크기에
    민감해서 잘라내기 위치가 바뀜 (실측 y 14~65% → y 50~100%). Letterbox/PanAndScan만 적용.
  - `ThumbnailBench.cs` (신규): `-benchthumb 폴더` 계측 스위치.
  - selftest 50 → 75개.
- **실측** (25MB급 PNG 7장, 16코어): 폴더 전체 1540→400ms(3.8배),
  JPEG 단장 84.6→24.7ms(3.4배). ADS 캐시 저장 항목 동일(226x337 hit).
- 한계: PNG 단일 디코딩은 오히려 약간 느림(WIC 축소 미지원, inflate 지배).
  PlaClip 축소 적용엔 해상도 독립 초점 계산이 필요 (미해결).

---

## v17-m1 — 리뷰 지적 반영 (직접 수정 + 빌드)

원본: `ZipPlaFork-v17-m1.zip` / 실행 폴더: `ZipPlaFork-v17-m1\`
(상세: `ZIPPLA_FORK_V17_M1.md`, 로그: `ZipPlaFork-v17-m1-selftest.log`,
`ZipPlaFork-v17-m1-thumbnailbench.log`)

1. **큐 대기 중 취소** (d2 리뷰 지적 #1)
   - `WeightedThrottle.AcquireAsync(units, CancellationToken)` 추가:
     취소 시 큐에서 제거 후 TaskCanceled, FIFO 유지, 리스 수령 후 취소는 반납.
   - `LoadAsync`가 `current.Token` 전달 (`Clear()`의 Cancel이 큐까지 전달됨).
   - `OperationCanceledException`은 로그 없이 조용히 종료 (스크롤 로그 폭증 방지).
   - 검사 8개 추가.
2. **작은 이미지 헤더 2회 파싱 제거** (지적 #5)
   - 축소 스킵 시에도 파싱된 `ImageInfo` 반환 → 폴백에서 재사용.
   - 검사 1개 추가.
3. 손대지 않음: PlaClip 제외, 3.5배 margin, 예산식 제한기, 벤치 방식.
   다음 후보: 벤치 순서 랜덤화·반복 중앙값, JPEG 수치 출처 명시.
- 검증: AutoBuild 오류 0 경고 0 / **selftest 84 통과 0 실패** /
  bench 재측정 old 1572·519ms → new 405·400ms (d2 수치 재현, 회귀 없음).

---

## v18 - 버전 자동화 (v18.1부터)

- 베이스: v17 (d2+m1). v16 감사 잔여분 중 남아 있던 AlertError lock(StartForm)+Invoke만 수정 (전용락+BeginInvoke).
- 버전: VERSION + ForkVersion.Current + Program.DisplayName을 창 제목(카탈로그/뷰어/About)에 표시.
- 빌드: Build-Fork.ps1이 VERSION 읽기→빌드→Desktop\\coding\\ZipPlaFork-v18.N 폴더+zip 생성→다음 패치로 증가.
- 상세: ZIPPLA_FORK_V18.md

