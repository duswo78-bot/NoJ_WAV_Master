# NoJ WAV Master v1 (v1.3.0)

FFmpeg 및 C# .NET 8.0 WPF 기반의 **실시간 Live FX 청음 & 원클릭 음원 마스터링 스튜디오** 애플리케이션입니다.

---

## 주요 기능

### 1. 원클릭 마스터링 엔진 (4가지 모드)
- **Clean Master (`20Hz–18kHz · -1.0dB`)**: `20Hz` 이하 초저역 및 `18kHz` 이상 비가청 대역(워터마크 대역) 컷과 Brickwall Limiter 적용
- **Loud (`-14 LUFS`)**: 스트리밍 플랫폼 표준 음압(`-14 LUFS`, `-1.0 dBTP`) 노멀라이징 및 대역 클리닝
- **Manual FX**: 우측 확장형 `FX Rack`을 통한 9종 스튜디오 이펙트 세부 수동 마스터링
- **Convert Only**: FX 바이패스 순수 포맷 변환 및 메타데이터 완전 초기화

### 2. 빠른 다듬기 바 (메인 화면 원클릭 토글)
- **Auto Trim Silence (앞뒤 무음 제거)**: 곡 중간의 의도적 정적 구간은 유지한 채 곡 시작과 끝의 불필요한 무음 구간만 자동으로 감지하여 제거하고, 시작 지점 클릭 노이즈 방지를 위한 `12~15ms` 마이크로 페이드 인을 적용합니다.
- **Smooth Fade Out (페이드 아웃)**: 곡 끝부분에 자연스러운 쿼터 사인(`qsin`) 곡선 페이드 아웃을 적용해 뚝 끊기는 현상을 방지합니다 (`FX Rack`을 열지 않고도 모든 엔진 모드에서 함께 사용 가능).

### 3. 수동 FX 스튜디오 랙 (`FX Rack`)
- **Dolby Atmos 3D Spatializer (`ATMOS 3D`)**
  - **Spatial 3D Depth (Elevation) (`0 ~ 100%`)**: Haas 심리음향 미세 시간차(`haas`)와 `11kHz` 고역 에어 쉘프(`treble`)를 결합해 입체적인 공간 깊이와 고도감 형성
  - **Surround Expansion (`100 ~ 220%`)**: `stereotools` Mid/Side 매트릭스 및 스테레오 베이스 확장으로 중앙 보컬 초점을 유지하면서 360° 서라운드 스테이지 확장
  - **LFE Sub-Bass Boost (`0.0 ~ +9.0 dB`)**: `60Hz` 초저역 LFE 대역(`bass`) 부스트로 시네마틱 서브우퍼 타격감 보강
- **Edge EQ Filter (Watermark Cut)**: Low Cut (`10 ~ 200 Hz`), High Cut (`10,000 ~ 22,000 Hz`)
- **Brickwall Ceiling Limiter**: True Peak 천장 제한 (`-3.0 ~ -0.1 dB`)
- **Loudness Normalizer (LUFS)**: 타겟 통합 음압 조절 (`-20 ~ -7 LUFS`)
- **Analog Tube Warmth (Saturation)**: 진공관 배음 드라이브 및 컴프레션 (`0 ~ 100%`)
- **Space Reverb & Room Echo**: 룸 딜레이 (`20 ~ 300 ms`) 및 잔향 블렌딩 (`5 ~ 70%`)
- **Stereo Image Widener**: 스테레오 이미지 확장 (`100 ~ 200%`)
- **Pitch & Speed Shift**: 반음 단위 키/템포 조절 (`-6 ~ +6 st`)
- **Silence Trim & Fade Out Detail**: 무음 판정 기준(`-65 ~ -30 dB`, 기본 `-50 dB`) 및 페이드 아웃 길이(`0.5 ~ 5.0 s`, 기본 `2.0 s`) 세부 조절

### 4. 실시간 Live FX 청음 엔진 & A/B 비교 플레이어
- **무지연 실시간 FX 프리뷰 (`LiveDspEngine`)**: 트랙을 넣는 즉시 최종 변환 전에도 `AFTER (Live FX)` 청음이 가능하며, 재생 중 어떤 슬라이더나 체크박스를 조작해도 재생 위치가 끊기거나 처음으로 돌아가지 않고 **약 `0.1초` 만에 변경된 사운드·파형·QC 지표가 실시간 반영**됩니다.
- **오버레이 파형 비교 (Waveform Diff)**: 원본과 마스터링본의 파형을 겹쳐 비교하며 증폭된 구간(`+Boosted`, `#4ADE80`)과 컷/리미팅된 구간(`-Cut / Limited`, `#F472B6`)을 직관적으로 시각화합니다.
- **EBU R128 오디오 QC 인스펙터**: 실시간으로 통합 음압(`LUFS`), `True Peak (dBTP)`, 다이내믹 레인지(`LRA`) 및 샘플레이트를 분석하여 품질 등급(`S · STUDIO PASS`, `A · STREAM READY`, `B · LEVEL WARN`, `C · CLIP RISK`)을 판정합니다.
- **트랙별 빠른 파일 관리**: 각 트랙 우측의 폴더 열기(`📂`) 버튼으로 탐색기에서 결과물 위치를 즉시 하이라이트하여 열 수 있으며, 개별 삭제(`✕`) 버튼으로 원하는 트랙만 목록에서 제거할 수 있습니다.

### 5. 스튜디오 키보드 단축키

| 단축키 | 동작 |
| :--- | :--- |
| `Space` | 오디오 재생 / 일시정지 |
| `Tab` 또는 `B` | `BEFORE (Raw)` ↔ `AFTER (Live FX)` 끊김 없이 즉시 교차 비교 |
| `←` / `→` | 현재 재생 위치에서 3초 뒤로 / 앞으로 탐색 (Seek) |
| `Delete` | 현재 선택된 트랙을 목록에서 개별 제거 |

### 6. 순수 오디오 내보내기 (7종 포맷)
- `WAV 24-bit`, `WAV 16-bit`, `FLAC 16-bit`, `FLAC 24-bit`, `MP3 320k`, `AIFF`, `AAC (.m4a)` (`44.1kHz Stereo`)
- 모든 메타데이터, 비오디오 스트림, 인코더 태그 완전 제거 (`-map_metadata -1 -fflags +bitexact -flags:a +bitexact`)

### 7. 무설치 단독 실행 지원
- `.NET 8` 런타임이 내장된 단일 실행 파일(`NoJ_WAV_Master_v1.exe`) 제공
- 로컬에 `ffmpeg.exe`가 없는 PC에서도 최초 실행 시 자동 다운로드 및 설치 지원 (오프라인 환경은 실행 파일과 같은 폴더에 `ffmpeg.exe` 동봉 시 자동 인식)

---

## 빌드 방법

```powershell
dotnet publish -c Release
```

빌드 완료 시 `bin/Release/net8.0-windows/win-x64/publish/NoJ_WAV_Master_v1.exe` 단일 파일이 생성됩니다.
