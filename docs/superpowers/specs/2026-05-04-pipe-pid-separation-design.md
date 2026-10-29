# Pipe / Structure PID 분리 설계

- 문서 버전: 1.0
- 작성일: 2026-05-04
- 상태: Draft (사용자 리뷰 대기)

## 1. 배경 및 목적

현재 `FeModelBuilder`는 `secAlloc = new IdAllocator(1)` 단일 카운터로 Structure / Pipe 단면 ID를 연속 발급한다. BDF 출력 시 `BeamSection.Id`가 그대로 `PBEAML` PID가 되므로, **BDF만 가지고는 어떤 PID가 Structure이고 어떤 것이 Pipe인지 구분할 수 없다.**

향후 외부 뷰어가 BDF만 입력으로 받아 모델을 재구성할 때 Structure / Pipe 카테고리를 시각적으로 구분해야 하므로, 단순하고 결정적인 분류 기준을 BDF에 박아 둘 필요가 있다.

## 2. 정책

**ID 범위로 분리한다.**

| 카테고리 | PID 범위 |
|----------|----------|
| Structure | `1 ~ PipePidBase - 1` |
| Pipe | `PipePidBase` 이상 |

`PipePidBase`는 `RunOptions.PipePidBase`로 노출하고 기본값은 **101**로 한다. 따라서 Structure는 `1~100`, Pipe는 `101`부터 할당한다.

Equipment(`EntityCategory.Equipment`)는 `BeamElement`를 생성하지 않으므로 PBEAML이 없어 분리 대상이 아니다 (RBE2 / CONM2만 생성).

## 3. 구현 변경

### 3.1 `Cmb.Pipeline.Core.RunOptions`

```csharp
public sealed record RunOptions(
    Tolerances Tolerances,
    string?    StopAfterStage = null,
    bool       RecordTrace    = true,
    int        PipePidBase    = 101)   // 신규
```

`Tolerances`와 별도의 최상위 필드로 둔다 — 수치 허용오차가 아니라 **출력 컨벤션**이기 때문.

### 3.2 `Cmb.Core.Building.FeModelBuilder`

- `Build(RawDesignData data, int pipePidBase = 101)` 시그니처에 매개변수 추가.
- `secAlloc` 1개 → 2개로 분리:
  - `structSecAlloc = new IdAllocator(1)`
  - `pipeSecAlloc   = new IdAllocator(pipePidBase)`
- `secMap` 도 카테고리별로 분리 (`structSecMap`, `pipeSecMap`) — 동일 dim·material이 양쪽에서 우연히 매칭돼도 PBEAML은 카테고리별로 따로 생성하도록.
- 가드: Structure section 발급 ID가 `pipePidBase`에 도달하면 즉시 throw.
  - 예외: `InvalidOperationException`
  - 메시지: `"Structure section count exceeded PipePidBase ({pipePidBase}). Increase RunOptions.PipePidBase."`

### 3.3 `Cmb.Io.Nastran.BdfWriter`

`Write` 시그니처에 `int pipePidBase = 1000` 추가. `WriteProperties`를 다음과 같이 출력:

```
$ HiTessModelBuilder: PipePidBase=101
$ ---- STRUCTURE SECTIONS (PID < 101) ----
PBEAML  1   1               H
+       0.05    0.01    0.05    0.01    0.0
PBEAML  2   1               L
...
$ ---- PIPE SECTIONS (PID >= 101) ----
PBEAML  101     2               TUBE
+       0.1     0.09    0.0
...
```

분류 기준은 **`section.Id >= pipePidBase`** — 빌더가 이미 ID 범위로 분리해 두었으므로 BdfWriter는 ID만 보고 그룹핑한다.

`$`는 Nastran 주석으로 해석에 영향 없음.

### 3.4 `Cmb.Cli.Program`

- `BdfWriter.Write(...)` 호출 모든 지점에 `pipePidBase: runOptions.PipePidBase` 전달.
- 현재로서는 CLI 옵션 노출하지 않음 (코드 기본값 101 사용). 필요해지면 추후 추가.

### 3.5 `README.md`

외부 뷰어가 BDF만으로 분리하는 가이드를 새 섹션(§10)으로 추가. marker 주석 형식, 파싱 의사코드, fallback 휴리스틱 명시.

## 4. 라운드트립 / JSON 영향

- `BeamSection.Id`가 그대로 직렬화되므로 JSON은 자동으로 새 PID 체계 반영. **schemaVersion 변경 없음** (1.2 유지).
- 기존 phase JSON / Final JSON 의 PID 값이 달라짐 → 회귀 단계 산출물 비교 시 baseline 갱신 필요.

## 5. 테스트 영향

- `BdfWriterTests` (Verify 골든 스냅샷): 기대 BDF가 marker 주석 + PID 체계 변경으로 깨짐 → 골든 갱신.
- `FeModelBuilderTests`: section ID 검증 케이스가 있다면 갱신.
- 신규 테스트:
  - Structure / Pipe 양쪽 PID 범위 검증 단위 테스트.
  - Structure section 수 ≥ `PipePidBase` 시 throw 검증.
  - BdfWriter 출력에 marker 주석 라인이 포함되는지 검증.

## 6. 비범위 (Out of Scope)

- Equipment RBE2 / CONM2 의 ID 분리 — 이번 작업 범위 외.
- CLI에서 `--pipe-pid-base` 노출 — 기본값 1000으로 모든 실 데이터 충분, 필요 시 후속.
- BDF re-import 파이프라인 (BDF → FeModel) — 별도 과제.

## 7. 결정 근거 요약

- **PipePidBase = 101**: 요구사항에 따라 Structure PID `1~100`을 예약하고 Pipe PID를 `101`부터 시작한다. Structure 단면이 100개를 초과하면 명시적으로 실패하며, 필요 시 옵션값을 확장할 수 있다.
- **빌드 시점 PID 분리** (BDF 출력 시 변환이 아님): JSON과 BDF 양쪽이 동일한 ID 체계를 갖게 되어 라운드트립 / 디버깅이 단순해짐.
- **`secMap` 카테고리별 분리**: dedup이 카테고리 경계를 넘으면 동일 dim·material section이 한쪽 카테고리로 흡수돼 분류가 깨질 수 있음. 약간의 PBEAML 중복(거의 없음)을 받아들이는 대신 분류 정확성을 우선.
- **`$` 주석 marker**: 사람이 BDF를 열어봐도 즉시 판별 가능. 임계값이 1000이 아닌 경우에도 viewer가 정확히 파싱.
