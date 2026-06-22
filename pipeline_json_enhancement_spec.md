# Pipeline JSON Schema Enhancement Specification

> 이 문서는 FEM Viewer (React + Three.js) 개발 과정에서 파악한 JSON 스키마 보강 사항을 정리한 것입니다.
> 파이프라인 담당 Claude Code 인스턴스가 이 문서를 참고하여 각 단계의 JSON 출력을 개선해 주세요.

---

## 배경

현재 파이프라인은 각 처리 단계(Stage)마다 JSON 파일을 생성하며, 뷰어는 이 JSON을 로드하여
3D 모델과 메타데이터를 표시합니다. 그러나 현재 JSON에는 다음 필드들이 비어 있거나 누락되어
뷰어가 **클라이언트에서 직접 재계산**해야 합니다:

- `healthMetrics` — 모든 스테이지에서 `null` 또는 불완전
- `connectivity` — 모든 스테이지에서 `null`
- `diagnostics` — 빈 배열 `[]` (진단 코드 카운트만 존재)
- `elements[].parentElemId` — 일부 스테이지에서만 존재

클라이언트 재계산은 가능하지만, 파이프라인이 이미 계산한 정보를 중복 계산하는 비효율이 있으며,
파이프라인만 알 수 있는 정보(예: 처리 시간, 검증 결과, CAD 소스 좌표 원점 등)는 클라이언트에서
재현이 불가능합니다.

---

## 1. `healthMetrics` 완전한 구조 출력

### 현재 상태
일부 스테이지에서 `healthMetrics`가 `null`이거나 `totals.bbox`만 포함.

### 요청 사항
모든 스테이지에서 아래 전체 구조를 출력해 주세요.

```json
"healthMetrics": {
  "totals": {
    "nodeCount": 1234,
    "elementCount": 987,
    "rigidCount": 12,
    "pointMassCount": 5,
    "elementsByCategory": {
      "Structure": 600,
      "Pipe": 387
    },
    "totalLengthMm": 45231.5,
    "lengthByCategoryMm": {
      "Structure": 28000.0,
      "Pipe": 17231.5
    },
    "bbox": {
      "minX": -5000, "maxX": 8000,
      "minY": -1000, "maxY": 3000,
      "minZ": 0,    "maxZ": 4500
    }
  },
  "issues": {
    "freeEndNodes": 42,
    "orphanNodes": 0,
    "shortElements": 3,
    "disconnectedGroups": 2,
    "unresolvedUbolts": 0
  },
  "diagnosticCounts": {
    "error": 0,
    "warning": 5,
    "info": 12,
    "byCode": {
      "FREE_END_NODE": 42,
      "SHORT_ELEMENT": 3,
      "GROUP_NOT_CONNECTED": 2
    }
  }
}
```

**주의사항:**
- `issues.freeEndNodes`: BEAM 요소에 한쪽만 연결된 노드 수 (degree=1)
- `issues.orphanNodes`: 어떤 BEAM에도 연결되지 않은 노드 수 (degree=0)
- `issues.shortElements`: 길이 < 1mm인 요소 수
- `issues.disconnectedGroups`: Union-Find 기준 연결 그룹 수 - 1 (0이면 완전 연결)

---

## 2. `connectivity` 완전한 구조 출력

### 현재 상태
모든 스테이지에서 `connectivity`가 `null`.

### 요청 사항
모든 스테이지에서 아래 구조를 출력해 주세요.

```json
"connectivity": {
  "groupCount": 3,
  "largestGroupNodeCount": 980,
  "largestGroupElementCount": 850,
  "largestGroupNodeRatio": 0.794,
  "isolatedNodeCount": 0,
  "groups": [
    { "id": 0, "nodeCount": 980, "elementCount": 850 },
    { "id": 1, "nodeCount": 200, "elementCount": 180 },
    { "id": 2, "nodeCount": 54,  "elementCount": 45  }
  ]
}
```

**주의사항:**
- `groups`는 요소 수 내림차순 정렬 (가장 큰 그룹이 id=0)
- `largestGroupNodeRatio` = largestGroupNodeCount / totalNodeCount
- `isolatedNodeCount`: 어떤 BEAM에도 연결되지 않은 노드 수

---

## 3. `diagnostics[]` 개별 항목 출력

### 현재 상태
`diagnostics: []` — 빈 배열. `healthMetrics.diagnosticCounts.byCode`에 집계만 존재.

### 요청 사항
각 진단 항목을 개별 오브젝트로 출력해 주세요. 뷰어에서 클릭 시 해당 요소/노드를 3D에서 강조할 수 있습니다.

```json
"diagnostics": [
  {
    "code": "FREE_END_NODE",
    "severity": "warning",
    "nodeId": 1023,
    "elemId": null,
    "message": "노드 1023은 하나의 BEAM에만 연결된 자유단입니다."
  },
  {
    "code": "SHORT_ELEMENT",
    "severity": "error",
    "nodeId": null,
    "elemId": 4512,
    "message": "요소 4512의 길이가 0.3mm로 최소 기준(1mm) 미만입니다."
  },
  {
    "code": "GROUP_NOT_CONNECTED",
    "severity": "warning",
    "nodeId": null,
    "elemId": null,
    "groupId": 2,
    "message": "그룹 2 (54개 노드)가 메인 구조와 연결되지 않았습니다."
  },
  {
    "code": "ORPHAN_NODE",
    "severity": "warning",
    "nodeId": 9999,
    "elemId": null,
    "message": "노드 9999는 어떤 요소에도 연결되지 않은 고립 노드입니다."
  }
]
```

**지원할 코드 목록 (우선순위 순):**

| code | severity | 설명 |
|------|----------|------|
| `FREE_END_NODE` | warning | 자유단 노드 (연결 degree=1) |
| `ORPHAN_NODE` | warning | 고립 노드 (연결 degree=0) |
| `SHORT_ELEMENT` | error | 길이 < 1mm 요소 |
| `GROUP_NOT_CONNECTED` | warning | 분리된 연결 그룹 |
| `DUPLICATE_NODE` | warning | 동일 좌표/허용오차 내 중복 노드 |
| `ZERO_LENGTH_ELEMENT` | error | 길이 = 0 요소 |

---

## 4. `elements[].length` 사전 계산 필드 추가

### 현재 상태
요소 길이는 뷰어가 `startNode`/`endNode` 좌표로 직접 계산.

### 요청 사항
각 BEAM 요소에 `lengthMm` 필드를 추가해 주세요.

```json
{
  "id": 3001,
  "type": "BEAM",
  "category": "Pipe",
  "startNode": 1001,
  "endNode": 1002,
  "propertyId": 10,
  "lengthMm": 482.7
}
```

**이점:** 뷰어 InspectorPanel에서 선택된 요소의 길이를 즉시 표시 가능. 현재는 노드 좌표 조회 후 계산.

---

## 5. 요소 생애주기 추적을 위한 ID 정책 (핵심)

> 이 섹션은 뷰어의 **Lifecycle(생애 추적)** 기능의 전제 조건입니다.
> 반드시 준수해 주세요.

### 5-A. 요소 ID 재사용 절대 금지

**규칙:** 파이프라인 전체 실행에서 한번 발급된 요소 ID는 **절대 재사용하지 않습니다.**

- 요소가 삭제(ElementRemoved)되어도 그 ID는 영구 폐기
- 분할(ElementSplit)로 생성된 자식 요소는 반드시 새 ID 발급
- 병합(NodeMerged) 후 재생성되는 요소도 반드시 새 ID 발급

**위반 시 문제:**
```
Stage 3: E1001 삭제됨
Stage 5: 새 요소가 E1001 ID로 생성됨
→ 뷰어에서 E1001 클릭 시 삭제된 요소와 새 요소가 혼합되어 추적 불가
```

### 5-B. 모든 변경에 새 ID 발급 — 불변(Immutable) ID 원칙

**규칙:** 요소의 **어떤 속성이든 변경이 발생하면 새 ID를 발급**하고, 기존 요소는 삭제 처리합니다.

변경 대상 속성 예시:
- `startNode` 또는 `endNode` 변경 (노드 병합, 좌표 보정)
- `propertyId` 변경 (단면 재지정)
- `category` 변경 (Pipe ↔ Structure)
- 분할로 인한 길이 변경

```
[AS-IS] 변경 전 요소를 같은 ID로 덮어쓰기
  Stage 3: E1001 { startNode: 10, endNode: 20 }
  Stage 4: E1001 { startNode: 10, endNode: 99 }  ← endNode만 바뀜, ID 유지
  결과: Stage 3의 E1001과 Stage 4의 E1001 구분 불가

[TO-BE] 변경 시 새 ID 발급 + 기존 삭제
  Stage 3: E1001 { startNode: 10, endNode: 20 }
  Stage 4: E1001 삭제됨 (ElementRemoved)
           E1500 생성됨 (ElementCreated, parentElemId: 1001)
           E1500 { startNode: 10, endNode: 99 }
  결과: 뷰어에서 E1500 클릭 → "부모: E1001 (Stage 3에서 삭제됨)" 추적 가능
```

### 5-C. `parentElemId` 모든 스테이지에 일관 적용

모든 요소에 `parentElemId` 필드를 명시적으로 포함해 주세요.

| 상황 | parentElemId 값 |
|------|----------------|
| 파이프라인 첫 단계에서 CAD로부터 직접 생성 | `null` |
| 기존 요소 변경/분할로 파생된 요소 | 직전 부모 요소의 ID |
| 여러 요소 병합으로 생성된 요소 | 가장 긴(주) 부모 요소의 ID |

```json
// Stage 1 - CAD 파싱 원본
{ "id": 1001, "type": "BEAM", "parentElemId": null, "startNode": 10, "endNode": 20 }

// Stage 4 - E1001의 endNode가 노드 병합으로 변경됨 → 새 ID E1500 발급
{ "id": 1500, "type": "BEAM", "parentElemId": 1001, "startNode": 10, "endNode": 99 }

// Stage 7 - E1500이 U-bolt 처리 중 두 요소로 분할됨 → 새 ID E1800, E1801 발급
{ "id": 1800, "type": "BEAM", "parentElemId": 1500, "startNode": 10, "endNode": 55 },
{ "id": 1801, "type": "BEAM", "parentElemId": 1500, "startNode": 55, "endNode": 99 }
```

### 5-D. `trace[]` 항목과의 연동

요소 변경 시 반드시 `trace[]`에 해당 이벤트를 기록해 주세요.

```json
"trace": [
  {
    "action": "ElementRemoved",
    "elemId": 1001,
    "stage": "NodeMergeResolution",
    "reason": "endNode 병합으로 요소 재생성"
  },
  {
    "action": "ElementCreated",
    "elemId": 1500,
    "parentElemId": 1001,
    "stage": "NodeMergeResolution",
    "reason": "E1001 재생성 (endNode 20→99 병합)"
  }
]
```

뷰어는 `trace[]`를 파싱하여 임의의 요소 ID에 대해 전체 생애주기를 재구성합니다.
`trace[]`와 `parentElemId`가 일치해야 정확한 추적이 가능합니다.

---

## 6. `meta.processingDuration` 추가

### 현재 상태
`meta`에 처리 시간 정보 없음.

### 요청 사항
```json
"meta": {
  "schemaVersion": "1.1",
  "stage": "FreeNodeResolution",
  "stageIndex": 3,
  "timestamp": "2026-04-24T17:29:24Z",
  "processingDurationMs": 142,
  "inputFile": "stage_02_output.json",
  "description": "자유단 노드 해소 단계"
}
```

- `stageIndex`: 0-based 정수 (파이프라인 순서)
- `processingDurationMs`: 이 단계 처리 소요 시간 (밀리초)
- `description`: 단계 설명 (한국어 가능)

---

## 7. 최종 스테이지에 `validationResult` 추가

### 현재 상태
최종 검증 결과가 JSON에 명시적으로 없음.

### 요청 사항
파이프라인 마지막 단계(FinalValidation 등) JSON에 추가:

```json
"validationResult": {
  "passed": true,
  "score": 94.5,
  "errorCount": 0,
  "warningCount": 5,
  "summary": "모델이 유효합니다. 자유단 노드 5개 주의 필요.",
  "checklist": [
    { "check": "connectivity",      "passed": true,  "detail": "단일 연결 그룹" },
    { "check": "noZeroLength",      "passed": true,  "detail": "길이=0 요소 없음" },
    { "check": "noOrphanNodes",     "passed": true,  "detail": "고립 노드 없음" },
    { "check": "boundaryCondition", "passed": true,  "detail": "경계조건 노드 12개 확인" },
    { "check": "massBalance",       "passed": false, "detail": "질량 중심 편차 5.2% (기준: 3%)" }
  ]
}
```

---

## 8. `nodes[].tags` 확장

### 현재 상태
`tags` 배열에 `"Boundary"`, `"Weld"` 등 일부 태그만 존재.

### 요청 사항
아래 태그들을 파이프라인이 자동 계산하여 추가해 주세요.

| 태그 | 조건 |
|------|------|
| `"FreeEnd"` | BEAM 연결 degree = 1인 자유단 |
| `"Orphan"` | BEAM 연결 degree = 0인 고립 |
| `"Intersection"` | BEAM 연결 degree ≥ 3인 교차점 |
| `"UboltAnchor"` | U-bolt 앵커로 지정된 노드 |
| `"Boundary"` | 경계조건 노드 (기존 유지) |
| `"Weld"` | 용접 연결 노드 (기존 유지) |

```json
{
  "id": 1023,
  "x": -2400.5, "y": 800.0, "z": 1200.0,
  "tags": ["FreeEnd"]
}
```

**이점:** 뷰어에서 클라이언트 재계산 없이 즉시 태그 기반 필터링 가능.

---

## 9. `stageDeltas` 추가 (2번째 스테이지부터)

### 현재 상태
각 스테이지가 이전 스테이지 대비 변경 사항을 명시하지 않음. 뷰어가 `trace[]`에서 직접 집계.

### 요청 사항
2번째 스테이지부터 `stageDeltas` 필드 추가:

```json
"stageDeltas": {
  "fromStage": "CADParsing",
  "toStage": "FreeNodeResolution",
  "elementsCreated": 45,
  "elementsRemoved": 12,
  "elementsSplit": 8,
  "nodesMerged": 23,
  "nodesMoved": 5,
  "netElementDelta": 33,
  "netNodeDelta": -18
}
```

**이점:** 뷰어 Trace 탭의 파이프라인 뷰에서 각 단계 바 차트를 즉시 구성 가능.

---

## 10. 최종 스테이지에 `stageHistory` 추가

### 현재 상태
최종 JSON에 전체 파이프라인 요약이 없어 개별 JSON을 순서대로 모두 로드해야 함.

### 요청 사항
마지막 스테이지 JSON에 전체 파이프라인 요약을 추가:

```json
"stageHistory": [
  {
    "stageIndex": 0,
    "stage": "CADParsing",
    "timestamp": "2026-04-24T17:29:10Z",
    "processingDurationMs": 85,
    "elementCount": 800,
    "nodeCount": 950,
    "issueCount": { "error": 0, "warning": 15, "info": 30 }
  },
  {
    "stageIndex": 1,
    "stage": "FreeNodeResolution",
    "timestamp": "2026-04-24T17:29:12Z",
    "processingDurationMs": 142,
    "elementCount": 820,
    "nodeCount": 930,
    "issueCount": { "error": 0, "warning": 8, "info": 25 }
  }
]
```

---

## 11. `elementQuality` 품질 분포 요약 추가

### 현재 상태
없음.

### 요청 사항
```json
"elementQuality": {
  "lengthDistribution": {
    "min": 0.3,
    "max": 8240.0,
    "mean": 482.7,
    "median": 320.0,
    "p10": 85.0,
    "p90": 1200.0
  },
  "shortElementCount": 3,
  "shortElementThresholdMm": 1.0,
  "aspectRatioOk": true
}
```

---

## 구현 우선순위

| 우선순위 | 항목 | 이유 |
|---------|------|------|
| 1 | `healthMetrics` 완전 출력 (#1) | 뷰어 HealthTab, DiffTable에서 필수 |
| 1 | `connectivity` 완전 출력 (#2) | 뷰어 ConnectivityTab에서 필수 |
| 2 | `diagnostics[]` 개별 항목 (#3) | 클릭-강조 기능 구현에 필수 |
| 1 | 요소 ID 재사용 절대 금지 (#5-A) | Lifecycle 추적의 전제 조건 — 위반 시 추적 불가 |
| 1 | 모든 변경 시 새 ID 발급 (#5-B) | 불변 ID 원칙 — 같은 ID가 다른 형태로 존재 불가 |
| 2 | `elements[].parentElemId` 전 스테이지 일관 적용 (#5-C) | Lifecycle 부모-자식 추적 |
| 2 | `trace[]` 이벤트와 parentElemId 일치 (#5-D) | 뷰어 생애주기 재구성 정확도 |
| 3 | `nodes[].tags` 확장 (#8) | 클라이언트 재계산 제거 |
| 3 | `elements[].lengthMm` (#4) | 클라이언트 재계산 제거 |
| 4 | `meta.processingDuration` (#6) | 성능 모니터링 |
| 4 | `stageDeltas` (#9) | 파이프라인 뷰 바 차트 최적화 |
| 5 | `validationResult` (#7) | 최종 검증 결과 표시 |
| 5 | `stageHistory` (#10) | 단일 JSON으로 전체 파이프라인 조회 |
| 5 | `elementQuality` (#11) | 품질 분포 시각화 |

---

## 뷰어 수신 측 처리 방식

뷰어(`viewer/src/data/StageData.js`)는 현재 다음 폴백 로직을 사용합니다:

```js
// 파이프라인이 제공하면 그대로 사용, 없으면 클라이언트 계산
if (!this.healthMetrics) this.healthMetrics = this._computeHealthMetrics()
if (!this.connectivity)  this.connectivity  = this._computeConnectivity()
```

파이프라인이 위 필드들을 올바르게 제공하면 클라이언트 계산을 건너뛰고
파이프라인 값을 그대로 사용합니다. **하위 호환성이 유지됩니다.**

---

## 현재 JSON 스키마 버전

```json
"meta": { "schemaVersion": "1.1", ... }
```

위 변경 사항 적용 후 `schemaVersion`을 `"1.2"`로 올려 주세요.
뷰어에서 버전 체크로 신규 필드 존재 여부를 확인할 수 있습니다.

---

*작성: FEM Viewer 개발 세션 | 2026-04-26*
