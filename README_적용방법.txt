QCS 툴바 적용 방법

1. ZIP 압축을 해제합니다.
2. QCS Visual Studio 프로젝트의 MainWindow.xaml을 제공 파일로 교체합니다.
3. Resources/Icons 폴더의 PNG 11개를 프로젝트의 동일 경로에 추가합니다.
4. Visual Studio 솔루션 탐색기에서 PNG 파일을 프로젝트에 포함하고 Build Action=Resource로 설정합니다.
5. MainWindow.xaml.cs는 수정하지 않습니다.
6. 빌드 후 실행하여 아이콘 및 클릭 이벤트를 확인합니다.

SVG 파일은 향후 아이콘 디자인 수정용 원본입니다. 실제 XAML에서는 PNG 파일을 사용합니다.
