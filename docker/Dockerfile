# FinAI in Docker — container parity with the local setup documented in SETUP.md.
#
# quantconnect/lean:foundation already ships everything env.sh sets up by hand:
#   .NET 10 SDK        DOTNET_ROOT=/root/.dotnet
#   Python 3.11        /opt/miniconda3
#   pythonnet          PYTHONNET_PYDLL=/opt/miniconda3/lib/libpython3.11.so
#   JupyterLab         /opt/miniconda3/bin/jupyter
# so there is no PYTHONNET_PYDLL/PYTHONHOME dance to repeat here.
#
# Pulling this image is a Docker Hub pull, not a QuantConnect API call: the
# container runs fully offline against local Data/, with no account or token.
FROM quantconnect/lean:foundation

WORKDIR /FinAI
COPY . /FinAI

# Launcher transitively references Engine, Research, Optimizer.Launcher and
# DownloaderDataProvider, so this single build covers every capability except
# the report generator and the web front end. Building the .sln instead would
# drag in Tests/. Web references no LEAN project — it drives the launcher as a
# child process — so it builds in seconds on top of the other two.
RUN dotnet build Launcher/QuantConnect.Lean.Launcher.csproj -c Release -v minimal \
 && dotnet build Report/QuantConnect.Report.csproj -c Release -v minimal \
 && dotnet build Web/QuantConnect.FinAI.Web.csproj -c Release -v minimal \
 && dotnet nuget locals all --clear \
 && rm -rf /FinAI/*/obj

# Research only. Backtests embed Python inside .NET (PYTHONNET_PYDLL handles
# that, and foundation sets it); notebooks go the other way and need the Python
# side of the bridge, which foundation does not ship. Versions match the host
# .venv so research behaves identically in both places.
# Placed after the build so editing it does not invalidate the build layer.
RUN pip install --no-cache-dir clr-loader==0.2.9 pythonnet==3.0.5

# Lean ships its own clr.py in bin/Release; PYTHONPATH must precede
# site-packages so it wins over the pip package's, as env.sh does on the host.
ENV PYTHONPATH=/FinAI/Launcher/bin/Release

COPY docker/research-entrypoint.sh /usr/local/bin/research-entrypoint.sh
RUN chmod +x /usr/local/bin/research-entrypoint.sh

WORKDIR /FinAI/Launcher/bin/Release
ENTRYPOINT ["dotnet", "QuantConnect.Lean.Launcher.dll"]
