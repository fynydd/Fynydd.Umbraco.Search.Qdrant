source clean.sh

rm -r src/nupkg
cd src
dotnet pack --configuration Release
cd ..

source clean.sh
