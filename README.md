# DualSense Studio

Aplicativo para Windows x64 que ajusta o DualSense padrão conectado por USB.

- **Modo nativo** com prévia ao vivo, controle de cor e brilho e calibração do limite do R2 pelo DirectInput.
- **Modo virtual** com saída Xbox 360, zonas mortas, curvas analógicas e mapeamento de botões. Usa ViGEmBus. A opção de ocultar o controle físico usa HidHide.
- **Perfis por jogo** e teste de dez segundos para medir a cadência dos relatórios USB e o processamento no app.

## Instalar

1. Baixe o [instalador para Windows x64](https://github.com/KingTUDO/dualsense-studio/releases/latest/download/DualSense-Studio-Setup-0.1.3-win-x64.exe).
2. Abra o instalador e siga as instruções. Depois, abra **DualSense Studio** pelo menu Iniciar e conecte o controle com um cabo USB de dados.

O instalador configura o aplicativo para o usuário atual. O modo nativo funciona com o DualSense conectado por USB. Se quiser usar a saída virtual ou ocultar o controle físico, consulte os links para ViGEmBus e HidHide no arquivo `LEIA-ME.txt`, na pasta de instalação.

O [ZIP portátil](https://github.com/KingTUDO/dualsense-studio/releases/latest) também está disponível. Extraia o ZIP e abra **DualSense Studio.exe**.

A calibração nativa aplica o limite do R2 a jogos que usam o DirectInput calibrado. A aba **Gatilhos** permite restaurar o ajuste original. O teste de delay mede até o processamento local ou o envio ao driver virtual.

## Compilar

Use o SDK do .NET 10 no Windows.

```powershell
dotnet build DualSenseStudio.csproj -c Release
```

Os avisos das bibliotecas distribuídas estão na pasta `Licencas`. Para gerar o instalador com o Inno Setup 6, execute `ISCC.exe /DPayloadDir=C:\caminho\do\pacote Installer.iss`, apontando para a pasta que contém o executável portátil e o `LEIA-ME.txt`.
