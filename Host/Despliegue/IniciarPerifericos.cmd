@echo off
rem ---------------------------------------------------------------------------
rem  Lanzador del servicio local de perifericos (pinpad + impresora).
rem
rem  Arranca SmartOne.Perifericos.Host.exe DESDE ESTA MISMA CARPETA: las DLL
rem  nativas de Ingenico (PCLService.dll y compania) solo se cargan si estan
rem  junto al ejecutable.
rem
rem  El token de servicio se toma de token.txt (una sola linea) que esta junto
rem  a este archivo; si no existe, de la variable de entorno PERIFERICOS_TOKEN.
rem
rem  Uso:  IniciarPerifericos.cmd             (a mano: deja la ventana abierta)
rem        IniciarPerifericos.cmd sinpausa    (lo usa la tarea programada)
rem
rem  Si el servicio ya esta corriendo no lanza una segunda copia: el reintento
rem  de cada minuto termina de inmediato y el .exe, ademas, se protege solo con
rem  un mutex, por si dos copias de este .cmd coinciden en el mismo instante.
rem
rem  Este archivo debe quedar en ASCII puro: lo lee cmd.exe con la pagina de
rem  codigos del sistema, asi que los acentos se verian mal.
rem ---------------------------------------------------------------------------
setlocal EnableExtensions
cd /d "%~dp0"

set "EXE=%~dp0SmartOne.Perifericos.Host.exe"
set "LOG=%~dp0host.log"

rem La tarea programada llama con "sinpausa": si la ventana se quedara esperando
rem una tecla, la tarea no veria terminar el proceso y no reintentaria.
set "PAUSA=1"
if /i "%~1"=="sinpausa" set "PAUSA="

if not exist "%EXE%" goto :SinEjecutable

rem tasklist recorta el nombre en la tabla, por eso se busca un trozo que si
rem sobrevive al recorte; el filtro ya garantiza que solo aparezca este .exe.
tasklist /FI "IMAGENAME eq SmartOne.Perifericos.Host.exe" /NH 2>nul | find /I "SmartOne.Perifericos" >nul
if not errorlevel 1 goto :YaEnMarcha

call :LeerToken
if errorlevel 1 goto :SinToken

call :LeerBridgeConfig
call :Cabecera

"%EXE%"
set "CODIGO=%ERRORLEVEL%"

echo.
echo  ------------------------------------------------------------
if "%CODIGO%"=="0" (
    echo   El servicio se cerro sin errores.
    echo   Si usted no lo detuvo, ya habia otra copia en marcha.
) else (
    echo   El servicio termino con codigo %CODIGO%.
    echo   Revise pinpad.log y host.log en esta misma carpeta.
)
echo  ------------------------------------------------------------
>>"%LOG%" echo [%DATE:~-10% %TIME%] el servicio termino con codigo %CODIGO%
if defined PAUSA pause
exit /b %CODIGO%

rem El host ya esta en marcha: no se arranca otro. Codigo 0 para que el
rem reintento automatico no quede como tarea fallida. No se anota en host.log:
rem el reintento es cada minuto y llenaria la bitacora de arranques que no hubo.
:YaEnMarcha
echo.
echo  ------------------------------------------------------------
echo   El servicio ya esta en marcha en esta sesion de Windows.
echo   No se arranca una segunda copia.
echo   Si cambio token.txt o SmartOne.Perifericos.Host.exe.config,
echo   detengalo y vuelvalo a arrancar para que los lea.
echo  ------------------------------------------------------------
if defined PAUSA pause
exit /b 0

:LeerToken
if exist "%~dp0token.txt" (
    set /p PERIFERICOS_TOKEN=<"%~dp0token.txt"
    set "ORIGEN_TOKEN=token.txt"
)
if not defined ORIGEN_TOKEN set "ORIGEN_TOKEN=variable de entorno PERIFERICOS_TOKEN"
if not defined PERIFERICOS_TOKEN exit /b 1
exit /b 0

:LeerBridgeConfig
if exist "%~dp0bridge-url.txt" set /p PERIFERICOS_BRIDGE_URL=<"%~dp0bridge-url.txt"
if exist "%~dp0bridge-terminal.txt" set /p PERIFERICOS_BRIDGE_TERMINAL=<"%~dp0bridge-terminal.txt"
if exist "%~dp0bridge-token.txt" set /p PERIFERICOS_BRIDGE_TOKEN=<"%~dp0bridge-token.txt"
if exist "%~dp0bridge-insecure.txt" set /p PERIFERICOS_BRIDGE_INSEGURO=<"%~dp0bridge-insecure.txt"
exit /b 0


:Cabecera
echo.
echo  ============================================================
echo   Servicio local de perifericos
echo  ============================================================
echo   Carpeta : %~dp0
echo   Token   : %ORIGEN_TOKEN%
echo   Inicio  : %DATE% %TIME%
if "%PERIFERICOS_BRIDGE_INSEGURO%"=="1" echo   Aviso   : Bridge sin TLS ^(modo inseguro^).
echo.
echo   Arrancando SmartOne.Perifericos.Host.exe ...
echo   Para detenerlo: Ctrl+C en esta ventana.
echo.
>>"%LOG%" echo [%DATE:~-10% %TIME%] inicio, token desde %ORIGEN_TOKEN%
exit /b 0


:SinEjecutable
echo.
echo  [ERROR] No se encuentra SmartOne.Perifericos.Host.exe en esta carpeta.
echo          Copie la carpeta de despliegue completa: el .exe, la DLL del
echo          modulo y las seis DLL nativas deben quedar JUNTAS, en la misma
echo          carpeta, sin subcarpetas.
echo.
>>"%LOG%" echo [%DATE:~-10% %TIME%] ERROR no se encontro SmartOne.Perifericos.Host.exe
if defined PAUSA pause
exit /b 1


:SinToken
echo.
echo  [ERROR] No hay token de servicio.
echo.
echo          Cree un archivo token.txt junto a este .cmd con una sola linea:
echo          el mismo valor de 32 caracteres o mas que usa la API de FastAPI
echo          en su variable PERIFERICOS_TOKEN.
echo.
echo          Alternativa: definir PERIFERICOS_TOKEN en el entorno del usuario.
echo          Use solo letras, numeros, punto, guion y guion bajo.
echo.
>>"%LOG%" echo [%DATE:~-10% %TIME%] ERROR no hay token de servicio
if defined PAUSA pause
exit /b 1
