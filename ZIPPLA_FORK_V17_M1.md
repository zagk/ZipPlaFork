# ZipPlaFork-v17-m1 변경 내역

- 베이스: `ZipPlaFork-v16-d2` (썸네일 병렬화 + 축소 디코딩)
- 이번 패키지: `ZipPlaFork-v17-m1`
- 작성일: 2026-09-23
- 명명 규칙: 빌드마다 `ZipPlaFork-v17-m1`, `m2`, `m3` … (짧은 버전명 유지)

## m1에서 손댄 것 (d2 리뷰에서 나온 지적)

### 1. 큐 대기 중 취소 (핵심)
- 문제: `ThumbViewerItem.Clear()` → `Cancel()` 을 해도, 제한기 큐에 들어가서
  기다리는 동안은 취소가 안 먹었다. 리스를 받고 나서야 취소 체크.
  빠르게 스크롤하면 화면 밖 항목들이 큐를 막고 보이는 항목이 밀림.
- 수정:
  - `WeightedThrottle.AcquireAsync(units, CancellationToken)` 추가.
    취소되면 큐에서 빼고 `TaskCanceled`로 끝냄 (FIFO 순서 유지).
    이미 리스를 받은 뒤의 취소는 리스 반납 (호출자 finally 중복 Dispose 안전).
  - `ThumbnailLimiter.AcquireAsync(path, token)` 오버로드 추가.
  - `ThumbViewerItem.LoadAsync` 가 `current.Token` 전달.
  - `OperationCanceledException` 은 로그 남기지 않고 조용히 종료
    (스크롤할 때마다 로그 쌓이는 것 방지).
- 검사 4개 추가: 큐 대기 취소, 슬롯 해제, 취소 뒤 정상 획득, 사전 취소 토큰.

### 2. 작은 이미지 헤더 2회 파싱 제거 (자잘함)
- 문제: 작은 이미지마다 `ImageInfo` 생성 → null 반환 → 폴백에서 또 생성.
- 수정: `GetScaledThumbnailSource` 가 건너뛸 때도 파싱된 `ImageInfo` 반환,
  `GetThumbnailSourceBitmap` 폴백에서 재사용.
- 검사 1개 추가: small image keeps header info.

### 3. 손대지 않은 것
- PlaClip 제외, 3.5배 margin, 예산식 제한기, 벤치 — d2 그대로.
- 다음 후보: 벤치 순서 랜덤화·반복 중앙값, JPEG 수치 출처 명시.

## 검사
- `ZipPla.exe -selftest` → **84 통과 / 0 실패** (d2 로그 75 + 취소 8 + 헤더재사용 1).
- `ZipPla.exe -benchthumb img-test` 재측정: d2 수치 그대로 재현
  (old 1T 1572 / 16T 519, new no-limit 405 / limiter 400 ms). 회귀 없음.
