# SlmapsServerPlugin

SCP: Secret Laboratory 서버의 현재 맵 시드를 slmaps.com에 알려서, 그 시드가 시드 지도 사이트에서 잠시 수집되거나 조회되지 않게 하는 LabAPI 플러그인입니다. 게임플레이나 플레이어에게 보이는 것은 바꾸지 않습니다.

문의와 등록 토큰 발급은 디스코드에서 합니다. https://discord.gg/AKWe9PvbGm

요구 사항은 SCP:SL 전용 서버 14.2.x, LabAPI 1.1.x, 서버에서 slmaps.com으로 나가는 HTTPS 연결입니다.

## 설치

1. `SlmapsServerPlugin.dll`을 LabAPI 플러그인 폴더에 넣습니다.
   - 모든 포트에 적용하려면 `LabAPI\plugins\global\`
   - 특정 포트에만 적용하려면 `LabAPI\plugins\7777\`처럼 포트 이름 폴더
   - LabAPI 폴더는 Windows에서 `%APPDATA%\SCP Secret Laboratory\LabAPI\`, Linux에서 `~/.config/SCP Secret Laboratory/LabAPI/`입니다.
2. 서버를 한 번 켜면 `LabAPI\configs\<포트>\SlmapsServerPlugin\config.yml`이 생깁니다.
3. 아직 등록 전이므로 등록하라는 경고가 한 번 뜨고, 서버는 아무것도 보내지 않습니다.

## 서버 등록

등록은 포트마다 따로 합니다. 서버 인스턴스를 여러 개 돌린다면 포트 수만큼 토큰을 받으세요.

1. 운영진에게 등록 토큰을 요청합니다. 토큰은 `slreg_`로 시작하고 한 번만 쓸 수 있으며 7일 뒤에 만료됩니다.
2. `config.yml`의 `registration_token`에 토큰을 넣습니다.
   ```yaml
   registration_token: slreg_여기에받은토큰
   ```
3. 서버 콘솔에서 `labapi reload configs`를 실행하거나 서버를 재시작합니다.
4. 콘솔에 `Registered with slmaps (serverId ...)`가 나오면 끝입니다. 같은 폴더에 `credential.yml`이 생기고 `registration_token`은 저절로 비워집니다. 이후로는 서버를 재시작해도 `credential.yml`로 계속 보고합니다.

`credential.yml`에는 이 서버 전용 자격이 들어 있습니다. 공유하지 마세요. slmaps는 해시만 보관하므로 파일을 잃어버리면 새 토큰으로 다시 등록해야 합니다.

자격이 폐기되면 콘솔에 오류가 나오고 보고가 멈춥니다. 새 토큰을 받아 `credential.yml`을 지우고, `registration_token`에 새 토큰을 넣은 뒤 `labapi reload configs`를 실행하세요.

## 설정

`config.yml`을 고친 뒤 `labapi reload configs`를 실행하면 바로 반영됩니다.

| 항목 | 기본값 | 설명 |
|---|---|---|
| `api_base_url` | `https://slmaps.com` | slmaps API 주소입니다. 안내가 없으면 그대로 두세요. |
| `registration_token` | `""` | 운영진이 발급한 1회용 등록 토큰입니다. 등록에 성공하면 저절로 비워지고, 정상적인 `credential.yml`이 있으면 쓰지 않습니다. |
| `report_interval_seconds` | `60` | 현재 시드를 주기적으로 알리는 간격입니다. `0`이면 주기 보고를 끄고, 1에서 9까지는 10으로 올려서 씁니다. |
| `send_round_id` | `false` | 맵이 생성될 때마다 새로 만드는 무작위 라운드 ID를 함께 보냅니다. |
| `send_round_start_time` | `false` | 라운드가 시작된 UTC 시각을 함께 보냅니다. 시작 전이면 비어 있습니다. |
| `send_elapsed_time` | `false` | 라운드 시작 후 지난 시간을 초 단위로 함께 보냅니다. 시작 전이면 비어 있습니다. |
| `request_timeout_seconds` | `10` | HTTP 요청 제한 시간입니다. 3에서 60 사이로 잘라 씁니다. |
| `debug` | `false` | 자세한 로그를 남깁니다. 토큰과 자격 값은 어떤 경우에도 남기지 않습니다. |

## 보내는 데이터

플러그인이 보내는 값은 아래가 전부입니다.

- 맵 시드
- 서버 포트
- 플러그인 버전, 게임 버전, LabAPI 버전
- 등록할 때만 등록 토큰
- 설정을 켰을 때만 라운드 ID, 라운드 시작 시각, 라운드 경과 시간

보고는 맵이 생성될 때, 라운드가 끝났을 때, 그리고 `report_interval_seconds` 간격으로 나갑니다. 요청은 게임 스레드를 막지 않는 백그라운드에서 한 번에 하나씩 나갑니다.

닉네임, SteamID, 플레이어 IP, 접속 인원, 채팅, 역할, 위치처럼 플레이어와 관련된 값은 하나도 보내지 않습니다. 서버 IP도 플러그인이 보내지 않고, slmaps가 요청이 들어온 주소로 봅니다.

## 문제 해결

| 콘솔에 나오는 말 | 뜻과 할 일 |
|---|---|
| `Not registered with slmaps: there is no credential.yml and registration_token is empty.` | 아직 등록하지 않았습니다. 위의 등록 절차를 따르세요. |
| `slmaps rejected registration_token (HTTP 401)` | 토큰이 틀렸거나 이미 썼거나 만료됐습니다. 새 토큰을 넣고 `labapi reload configs`를 실행하세요. 그 전에는 다시 시도하지 않습니다. |
| `slmaps rejected the registration request as invalid (...)` | 토큰 값을 다시 확인하고 `labapi reload configs`를 실행하세요. |
| `Registration failed (...); retrying in Ns.` 또는 `Report ... failed (...); retrying in Ns.` | 네트워크 오류나 일시적인 장애입니다. 5초, 15초, 60초, 그 뒤 300초 간격으로 알아서 다시 시도합니다. |
| `slmaps rejected the server credential (HTTP 401)` | 자격이 폐기됐거나 더 이상 없습니다. 위의 자격 복구 절차를 따르세요. |
| `Registered, but ... could not be written.` | 자격을 파일로 저장하지 못했습니다. 서버를 재시작하면 등록이 풀리니 폴더 권한을 확인하고 다시 등록하세요. |
| `Report queue is full; dropped N old round event(s).` | slmaps에 오래 연결되지 않아 밀린 보고 일부를 버렸습니다. 연결이 돌아오면 저절로 정상으로 돌아옵니다. |
| `api_base_url is not a valid http(s) URL.` | 주소를 고치고 `labapi reload configs`를 실행하세요. |
| `The server answered with a redirect; check api_base_url` | 주소가 잘못돼 리디렉트가 왔습니다. 보통 `https://slmaps.com`이어야 합니다. |

## 빌드

.NET SDK와 게임 서버의 `SCPSL_Data\Managed` 폴더가 필요합니다. NuGet이나 인터넷 연결은 필요 없습니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -ManagedDir "D:\SCPSL\SCPSL_Data\Managed"
```

결과물은 `bin\SlmapsServerPlugin.dll`입니다. 게임 없이 로직만 확인하려면 다음을 실행하세요.

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
```

## English

A LabAPI plugin that reports the server's current map seed to slmaps.com so that the seed is left out of the seed map site for a short while. It changes nothing players can see.

Install: drop `SlmapsServerPlugin.dll` into `LabAPI\plugins\global\` or `LabAPI\plugins\<port>\`, start the server once to create `config.yml`, then register the server.

Register: ask staff for a registration token, put it into `registration_token` in `config.yml` and run `labapi reload configs`. The token is single use and is cleared once the server is registered.

Sent data: the map seed, the server port, the plugin, game and LabAPI versions, and the optional round id, round start time and elapsed time. No player data is ever sent.

Discord: https://discord.gg/AKWe9PvbGm
