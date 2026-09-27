# ZipPlaFork-v16-d2 변경 내역

- 원본: `ZipPlaFork-v16.zip` (내부 폴더 `ZipPlaFork-v16-layout8-no-xmp-r4-row-copy`)
- 이전 패키지: `ZipPlaFork-v16-d1`
- 이번 패키지: `ZipPlaFork-v16-d2`
- 작성일: 2026-09-23
- 빌드: Visual Studio 2026 (18.0) MSBuild, `AutoBuild|Any CPU` 및 `Release|Any CPU`
  - 오류 0, 경고 0
  - 결과물: `source\ZipPla\bin\AutoBuild\ZipPla.exe` (배포 권장), `source\ZipPla\bin\General\ZipPla.exe`
- 자체 검사: `ZipPla.exe -selftest` → **75 통과 / 0 실패** (d1: 50개 → 75개, 연속 5회 실행 안정)
- 계측: `ZipPla.exe -benchthumb "폴더"` 로 썸네일 생성 속도를 직접 잴 수 있습니다.
- d1 대비 변경분: `ZipPlaFork-v16-d2-changes.diff` (d1 zip 과의 diff, 소스 451줄)

---

## 0. 결론 먼저 (실측값)

이번 빌드의 주제는 **썸네일 생성 속도**입니다. 사용자의 실제 파일(25MB 전후 AI 생성 PNG 7장, 16코어)로 측정했습니다.

| 상황 | 이전 (d1) | 이번 (d2) | 배수 |
| --- | --- | --- | --- |
| PNG 폴더 7장, 폴더 전체 | 1,540 ms | **400 ms** | 3.8배 |
| JPEG 6000x4000 한 장 | 84.6 ms | **24.7 ms** | 3.4배 |
| JPEG 2장 폴더 전체 | 117 ms | **26 ms** | 4.5배 |
| ADS 썸네일 캐시 | 그대로 (저장 크기·히트 동일) | 그대로 | — |
| 잘라내기 위치 (기본 PlaClip 모드) | 그대로 | 그대로 | — |

즉 **"16코어인데 썸네일이 한 장씩밖에 안 만들어지던" 문제**가 핵심이었고, 그것을 고친 것이 대부분입니다.

---

## 1. 원인: 썸네일 읽기가 전역 1개짜리 세마포어로 직렬화되어 있었음

`CatalogForm.cs` 의 `ThumbViewerItem` 에 이런 코드가 있었습니다.

```csharp
public class ThumbViewerItem : IDisposable
{
    static readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);   // static → 프로세스 전역
    ...
    public async Task LoadAsync()
    {
        ...
        await semaphore.WaitAsync();          // 항상 1장만
        Image = await Task.Run(() => OwnerCatalog.GetThumbnail(...));
        ...
    }
}
```

- `static` 이므로 **모든 창, 모든 항목이 이 세마포어 하나를 공유**합니다. 동시에 디코딩되는 이미지는 언제나 1장입니다.
- 이 포크는 썸네일 읽기를 `bmwMakePreview`(BackgroundMultiWorker) 에서 `ThumbViewerItem` 으로 옮겼기 때문에,
  `bmwMakePreview.ThreadCount = ProcessorCount / 2` 설정은 **죽은 코드**가 되어 있었습니다.
- 게다가 그 1장조차 **원본 전체 해상도**로 디코딩하고 있었습니다(56MP PNG → 168MB 비트맵 → 그 다음 160x120 으로 축소).

## 2. 원인: 원본 전체 디코딩

`GetImageThumbnail` 은 캐시가 없으면 언제나 `ImageLoader.GetFullBitmap(zipPath)` 를 불렀습니다.
160x120 썸네일 하나를 만들기 위해 56MP(24,000배 많은 픽셀)를 다 푸는 셈이었습니다.
특히 JPEG 은 libjpeg 의 DCT 스케일링(1/2, 1/4, 1/8)을 쓰면 1/8 크기로 바로 디코딩할 수 있는데도 그렇게 하지 않았습니다.

---

## 3. 수정 내용

### 3-1. `ThumbnailLimiter.cs` (신규) — 메모리 기준 동시 실행 제한기

"동시 작업 수" 가 아니라 **동시에 디코딩 중인 픽셀량**을 기준으로 제한합니다.
56MP 한 장은 디코딩 중에만 수백 MB를 쓰기 때문에, 그냥 병렬화하면 메모리가 터집니다.

- `WeightedThrottle`: 가중치 기반 FIFO 게이트. `AcquireAsync(단위)` → 리스 반환, `Dispose` 로 반납.
  - 대기열 맨 앞부터 순서대로 통과(새치기 금지) → 큰 작업이 굶지 않음
  - 리스를 두 번 Dispose 해도 예산이 망가지지 않음(Interlocked 로 1회만 반납)
  - 혼자서는 항상 통과(단위를 capacity 로 clamp) → 영원히 대기하지 않음
- 예산: 실제 물리 메모리의 1/16, 하한 384MB / 상한 1536MB
- 동시 실행 수 상한: `min(ProcessorCount, 16)`, 그리고 각 작업의 최소 가중치를 `ceil(capacity / 상한)` 로 두어 상한을 넘지 못하게 함
- 작업 가중치: 이미지 헤더에서 픽셀 수를 읽어 `픽셀 × 4바이트`. 압축 파일/동영상 등 형식을 모르면 256MB로 큰 작업 취급(디스크 포화 방지), 이미지인데 헤더가 안 읽히면 16MB.

### 3-2. `ThumbViewerItem.LoadAsync` — 전역 세마포어 제거

```diff
- static readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
- await semaphore.WaitAsync();
- Image = await Task.Run(() => OwnerCatalog.GetThumbnail(...));
+ lease = await ThumbnailLimiter.AcquireAsync(path);      // 대기는 비동기, 스레드풀을 막지 않음
+ if (current.IsCancellationRequested) return;
+ var thumbnail = await Task.Run(() => OwnerCatalog.GetThumbnailForThumbViewerItem(path));
+ if (current.IsCancellationRequested || FilePath != path) { thumbnail?.Dispose(); return; }
+ Image = thumbnail;
```

- 대기를 `SemaphoreSlim.WaitAsync` 가 아니라 async 게이트로 처리하므로 스레드풀 스레드가 잠기지 않습니다.
- 대기 중에 화면에서 벗어난 항목은 취소되고, 디코딩이 끝난 뒤에도 경로가 바뀌었으면 결과를 버립니다.
- 예외를 삼키지 않고 로그에 남깁니다(`Program.LogException`).

### 3-3. `ImageLoader.GetScaledThumbnailSource` (신규) — WIC/WPF 축소 디코딩

`BitmapImage.DecodePixelWidth/DecodePixelHeight` 로 **필요한 밀도만 남기고 디코딩**합니다.
이 프로젝트는 이미 `PresentationCore`/`WindowsBase` 를 참조하고 `BitmapResizer` 에서 WPF 를 쓰고 있으므로 새 의존성은 없습니다.

- 대상: `jpg / jpeg / png / bmp` (앱이 `IsLowLoad` 로 취급하는, 즉 캐시가 원본 크기를 보관하지 않는 형식만)
- 디코딩 크기: 요청 크기의 **3.5배**를 두 축 모두 유지(아래 3-5 참고).
  - 이 3.5는 `GPSizeThumbnail.TrySet` 이 요구하는 "원본 ≥ 축소 목표×2" 조건과
    `TryGet` 의 "요청×1.414 이상" 조건을 **모두** 만족합니다. 그래서 캐시에 저장되는 내용이 동일합니다.
- JPEG 은 방향(EXIF orientation)을 **축소한 뒤에** 적용합니다. 예전에는 원본 크기 비트맵을 통째로 돌렸습니다.
- 파일 정보(해상도/비트심도)는 헤더에서 읽으므로 표시되는 값이 바뀌지 않습니다.
- 실패하면 예외를 던지지 않고 기존 전체 디코딩 경로로 넘어갑니다.

### 3-4. `CatalogForm` — 위 경로 연결

`GetImageThumbnail` 의 캐시 사용/미사용 두 분기 모두에서 축소 디코딩을 먼저 시도하고,
불가능하거나 실패하면 기존 경로(`GetFullBitmap` / `GetAtLeastThumbnailBitmap`)를 그대로 씁니다.

### 3-5. 잘라내기 모드는 지키고, 굳이 축소 디코딩을 쓰지 않는 경우를 남김

기본 잘라내기 모드 `ClipMode.PlaClip` 은 소스에서 **초점점을 찾아 그 주변을 잘라내는** 모드입니다.
초점점 계산(`BitmapAnalyzer.GetFocus`)은 내부적으로 128px 로 줄여서 계산하는데,
그 결과가 **입력 해상도에 민감**합니다. 실제로 측정해 보니:

```
파일 69448d12-...png (4896x7296), 화면에 보이는 세로 구간
  원본 그대로 (지금 동작)        : y  14.3% ~  64.6%
  ADS 캐시 226px (지금 재방문)   : y  14.0% ~  64.3%
  560px 로 줄인 소스 (축소 디코딩): y  49.6% ~ 100.0%   ← 완전히 다른 부분이 보임
```

그림의 절반이 바뀌는 것은 속도와 맞바꿀 수 없으므로,
**초점점을 쓰지 않는 `Letterbox` / `PanAndScan` 모드에서만 축소 디코딩을 사용**합니다.

- `PlaClip`(기본): 예전과 완전히 동일한 잘라내기. 대신 병렬화 이득(3.8배)은 그대로 받습니다.
- `Letterbox` / `PanAndScan`: 잘라내기가 소스 크기에 비례하므로 화면 결과가 동일하며, 여기서 축소 디코딩 이득을 받습니다.
- 실측 근거는 `ZipPlaFork-v16-d2-thumbnailbench.log` 참고.

### 3-6. `ZipPla.exe -benchthumb "폴더"` (신규, 진단용)

`ThumbnailBench.cs`. 실제 폴더를 지정하면 다음을 재서 보여줍니다.

- 파일별: 전체 디코딩 / 축소 디코딩 / 각각 + 축소까지의 시간, 원본 픽셀 수
- 폴더 전체: 전체 디코딩 파이프라인(1스레드/N스레드), 축소 디코딩 파이프라인(제한기 켜기/끄기)
- `PlaClip` 모드에서 **화면에 보이는 세로 구간**을 원본 / 캐시 / 축소 소스별로 비교
- ADS 캐시에 실제로 저장되는 크기와 `TryGet` 히트 여부를 축소 디코딩 전후로 비교

결과는 표준 출력과 `%TEMP%\ZipPlaThumbnailBench.log` 에 남습니다.

### 3-7. `SelfTest.cs` — 검사 18개 추가 (50 → 75)

- `WeightedThrottle`: 예산 초과 시 대기, 해제 시 대기자 통과, FIFO 순서, 이중 Dispose 안전, capacity clamp
- `ThumbnailLimiter`: 동시 실행 수가 상한을 넘지 않음, 거대 작업도 혼자서는 통과(교착 방지)
- `GetScaledThumbnailSource`: 지원 형식 판정(대소문자 포함), 요청 크기의 3.5배 이상 밀도 유지, 종횡비 유지,
  헤더 기준 정보 유지, 원본이 작으면 사용하지 않음

덧붙여 이번 검사기가 **자체 테스트 하나의 결함**도 잡아냈습니다.
FIFO 순서 검사에서 두 작업이 동시에 통과할 수 있는 크기였기 때문에 완료 통지 순서가
스레드풀 사정에 따라 뒤바뀌어 간헐적으로 실패했습니다(재실행에서 실제로 1회 실패).
두 작업이 동시에 들어가지 못하는 크기로 고쳐서 실행 순서를 확정했습니다.

---

## 4. 캐시(ADS) 회귀 검증

축소 디코딩은 `GPSizeThumbnail.TrySet` 에 넘기는 비트맵을 작게 만들기 때문에,
캐시에 저장되는 내용이 달라지면 **두 번째 방문이 느려지는 회귀**가 생깁니다. 그래서 직접 확인했습니다.

```
old source (full decode)   : TrySet=ok  ads=16462 bytes  TryGet=hit  cached=226x337
new source (scaled decode) : TrySet=ok  ads=16127 bytes  TryGet=hit  cached=226x337
```

- 저장되는 항목 크기(226x337)와 히트 여부가 동일합니다.
- 용량 차이는 JXR 재압축 오차 수준입니다.
- 실행 중인 앱이 실제로 ADS 를 쓰는 것도 확인했습니다(`ZipPla.Thumbnail` 스트림 생성 확인).

## 5. 메모리

- 이전: 직렬 처리라 56MP 1장(수백 MB)씩만 사용.
- 이번: 예산(1536MB)에 맞춰 **56MP PNG 6장까지 동시**, 일반 사진은 코어 수(16)까지 동시.
  25MB PNG 7장 폴더를 여는 동안 실측 최대 작업 집합 약 686MB로 동작했습니다.
- 형식을 알 수 없는 항목(압축 파일 등)은 256MB 가중치로 취급해 디스크를 동시에 긁지 않도록 했습니다.

## 6. 남은 한계 / 다음에 할 만한 것

- **PNG 자체는 더 빠르게 못 만듭니다.** 25MB PNG 한 장의 디코딩은 inflate 가 지배적이고(전체 시간의 약 80%),
  WIC 도 PNG 축소 디코딩은 지원하지 않습니다. 실측으로 축소 디코딩 이득은 PNG 약 12%, JPEG 3.4배였습니다.
  PNG 를 더 줄이려면 직접 행 단위 PNG 디코더를 쓰거나 zlib 을 네이티브로 호출해야 하는데, 위험 대비 이득이 작아 하지 않았습니다.
- **PlaClip 모드에서도 축소 디코딩을 쓰려면** 초점점 계산을 해상도 독립적으로 만들어야 합니다.
  (`GetFocus` 의 128px 작업 이미지 생성이 소스 크기에 따라 결과가 흔들림 — 이번에 두 가지 필터로 시도했지만
  안정화되지 않아 되돌렸습니다.)
- 썸네일 읽기 외에도 원본 전체를 디코딩하는 경로가 남아 있습니다(전체 화면 표시, 미리보기 등). 그쪽은 별개입니다.
