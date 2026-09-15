# SlmapsServerPlugin

SCP: Secret Laboratory 서버의 현재 맵 시드를 slmaps.com에 알려서, 그 시드가 시드 지도 사이트에서 잠시 수집되거나 조회되지 않게 하는 LabAPI 플러그인입니다. 게임플레이나 플레이어에게 보이는 것은 바꾸지 않습니다.

문의와 인증 코드 발급은 디스코드에서 합니다. https://discord.gg/AKWe9PvbGm

요구 사항은 SCP:SL 전용 서버 14.2.x, LabAPI 1.1.x, 서버에서 slmaps.com으로 나가는 HTTPS 연결입니다.

## 설치

1. `SlmapsServerPlugin.dll`을 LabAPI 플러그인 폴더에 넣습니다.
   - 모든 포트에 적용하려면 `LabAPI\plugins\global\`
   - 특정 포트에만 적용하려면 `LabAPI\plugins\7777\`처럼 포트 이름 폴더
   - LabAPI 폴더는 Windows에서 `%APPDATA%\SCP Secret Laboratory\LabAPI\`, Linux에서 `~/.config/SCP Secret Laboratory/LabAPI/`입니다.
2. 서버를 한 번 켜면 `LabAPI\configs\<포트>\SlmapsServerPlugin\config.yml`이 생깁니다.
3. 아직 등록 전이므로 등록하라는 경고가 한 번 뜨고, 서버는 아무것도 보내지 않습니다.

## 서버 등록

등록은 포트마다 따로 합니다. 서버 인스턴스를 여러 개 돌린다면 포트 수만큼 등록하세요.

### 디스코드 인증

서버 주인인지는 그 서버에서 플러그인이 코드를 제출하는 것으로 확인합니다.

1. 디스코드에서 `/server claim address:<서버IP>:<포트>`를 실행합니다. 예를 들면 `/server claim address:203.0.113.10:7777`입니다.
   플레이어가 접속하는 공인 IP와 포트를 적고, IPv6는 `[주소]:포트`로 적습니다. 봇이 본인에게만 보이는 코드를 주고, 코드는 30분 동안 쓸 수 있습니다.
2. 코드를 서버에 넣습니다. 둘 중 편한 쪽을 쓰세요.
   - 서버 콘솔에서 `slmaps claim slclm_받은코드`를 실행합니다. 재시작이 필요 없습니다.
   - 또는 `config.yml`의 `claim_code`에 코드를 넣고 `labapi reload configs`를 실행하거나 서버를 재시작합니다.
3. 요청이 들어온 IP와 포트가 적은 주소와 같으면 바로 등록되고 콘솔에 `slmaps verified this server (serverId ...)`가 나옵니다. 다르면 운영진이 직접 확인하며, 서버를 켜 둔 채 기다리면 플러그인이 1분마다 다시 물어보고 승인되는 대로 등록됩니다.
4. `/server list`로 내 서버와 진행 중인 인증을 보고, `/server cancel`이나 `/server revoke`로 취소하거나 등록을 폐기할 수 있습니다.

코드는 봇 메시지의 코드 칸에 있는 값이고 항상 `slclm_`로 시작합니다. 따옴표나 꺾쇠로 감싸지 말고 코드만 넣으세요.

등록 뒤 보고는 인증된 IP에서 온 것만 받습니다. 서버 IP가 바뀌면 보고가 거부되고 디스코드로 알려 주며, 다시 인증해야 합니다. 같은 IP와 포트로 새로 인증하면 이전 등록은 폐기됩니다. 콘솔에 입력한 명령은 LocalAdmin 로그에 그대로 남으니 로그를 공유할 때 주의하세요.

### 등록 토큰

디스코드를 쓰기 어려우면 운영진에게 등록 토큰을 요청할 수 있습니다. 토큰은 `slreg_`로 시작하고 한 번만 쓸 수 있으며 7일 뒤에 만료됩니다.

1. `config.yml`의 `registration_token`에 토큰을 넣습니다.
   ```yaml
   registration_token: slreg_여기에받은토큰
   ```
2. 서버 콘솔에서 `labapi reload configs`를 실행하거나 서버를 재시작합니다.
3. 콘솔에 `Registered with slmaps (serverId ...)`가 나오면 끝입니다. 같은 폴더에 `credential.yml`이 생기고 `registration_token`은 저절로 비워집니다.

`credential.yml`에는 이 서버 전용 자격이 들어 있습니다. 공유하지 마세요. slmaps는 해시만 보관하므로 파일을 잃어버리면 다시 인증하거나 새 토큰으로 등록해야 합니다.

## 설정

`config.yml`을 고친 뒤 `labapi reload configs`를 실행하면 바로 반영됩니다.

| 항목 | 기본값 | 설명 |
|---|---|---|
| `api_base_url` | `https://slmaps.com` | slmaps API 주소입니다. 안내가 없으면 그대로 두세요. |
| `registration_token` | `""` | 운영진이 발급한 1회용 등록 토큰입니다. 등록에 성공하면 저절로 비워지고, 정상적인 `credential.yml`이 있으면 쓰지 않습니다. |
| `claim_code` | `""` | `/server claim`으로 받은 인증 코드입니다. `registration_token`보다 먼저 쓰고, 인증되면 기존 자격을 바꾼 뒤 저절로 비워집니다. |
| `report_interval_seconds` | `60` | 현재 시드를 주기적으로 알리는 간격입니다. `0`이면 주기 보고를 끄고, 1에서 9까지는 10으로 올려서 씁니다. |
| `send_round_id` | `false` | 맵이 생성될 때마다 새로 만드는 무작위 라운드 ID를 함께 보냅니다. |
| `send_round_start_time` | `false` | 라운드가 시작된 UTC 시각을 함께 보냅니다. 시작 전이면 비어 있습니다. |
| `send_elapsed_time` | `false` | 라운드 시작 후 지난 시간을 초 단위로 함께 보냅니다. 시작 전이면 비어 있습니다. |
| `request_timeout_seconds` | `10` | HTTP 요청 제한 시간입니다. 3에서 60 사이로 잘라 씁니다. |
| `debug` | `false` | 자세한 로그를 남깁니다. 토큰과 자격 값은 어떤 경우에도 남기지 않습니다. |

## 콘솔 명령

서버 콘솔에서 실행하며 결과는 영어로 나옵니다.

| 명령 | 하는 일 |
|---|---|
| `slmaps status` | 등록 상태, serverId, 진행 중인 인증, 현재 시드, `api_base_url`을 보여 줍니다. |
| `slmaps claim <코드>` | `/server claim`으로 받은 코드로 인증합니다. 자격이 이미 있어도 보고를 계속하면서 뒤에서 인증하고, 인증되면 새 자격으로 바꿉니다. |

## 보내는 데이터

플러그인이 보내는 값은 아래가 전부입니다.

- 맵 시드
- 서버 포트
- 플러그인 버전, 게임 버전, LabAPI 버전
- 등록할 때만 등록 토큰이나 인증 코드
- 설정을 켰을 때만 라운드 ID, 라운드 시작 시각, 라운드 경과 시간

보고는 맵이 생성될 때, 라운드가 끝났을 때, 그리고 `report_interval_seconds` 간격으로 나갑니다. 요청은 게임 스레드를 막지 않는 백그라운드에서 한 번에 하나씩 나갑니다.

닉네임, SteamID, 플레이어 IP, 접속 인원, 채팅, 역할, 위치처럼 플레이어와 관련된 값은 하나도 보내지 않습니다. 서버 IP도 플러그인이 보내지 않고, slmaps가 요청이 들어온 주소로 봅니다.

## 문제 해결

| 콘솔에 나오는 말 | 뜻과 할 일 |
|---|---|
| `Not registered with slmaps: there is no credential.yml, claim_code is empty and registration_token is empty.` | 아직 등록하지 않았습니다. 위의 등록 절차를 따르세요. |
| `claim_code is longer than 128 characters.` | 코드가 아닌 값이 들어 있거나 코드가 잘못 붙여졌습니다. 봇이 준 `slclm_` 코드만 넣고 `labapi reload configs`를 실행하세요. |
| `slmaps staff must review this claim manually (...)` | 요청 IP나 포트가 적은 주소와 달라 운영진 확인을 기다립니다. 서버를 켜 두면 플러그인이 알아서 다시 물어봅니다. |
| `slmaps rejected the claim code (HTTP 401)` | 코드가 틀렸거나 만료됐거나 이미 쓰였습니다. `/server claim`으로 새 코드를 받으세요. |
| `slmaps staff rejected this claim (HTTP 403)` | 운영진이 인증을 거절했습니다. 사유는 디스코드로 옵니다. |
| `slmaps rejected registration_token (HTTP 401)` | 토큰이 틀렸거나 이미 썼거나 만료됐습니다. 새 토큰을 넣고 `labapi reload configs`를 실행하세요. |
| `Registration failed (...); retrying in Ns.` 또는 `Report ... failed (...); retrying in Ns.` | 네트워크 오류나 일시적인 장애입니다. 5초, 15초, 60초, 그 뒤 300초 간격으로 알아서 다시 시도합니다. |
| `slmaps rejected the server credential (HTTP 401)` | 자격이 폐기됐거나 다른 인증으로 바뀌었거나 인증된 IP가 아닌 곳에서 보냈습니다. `/server claim`으로 다시 인증하세요. |
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

Register: run `/server claim address:<ip>:<port>` in the slmaps Discord and enter the code with `slmaps claim <code>` in the server console, or ask staff for a registration token and put it into `registration_token`.

Sent data: the map seed, the server port, the plugin, game and LabAPI versions, and the optional round id, round start time and elapsed time. No player data is ever sent.

Console commands: `slmaps status`, `slmaps claim <code>`.

Discord: https://discord.gg/AKWe9PvbGm
