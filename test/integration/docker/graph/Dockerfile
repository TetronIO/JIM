# syntax=docker/dockerfile:1
# jim-compliance: test-image
# Test infrastructure only; this image is never shipped to customers. It stands in for Microsoft Entra ID's token
# service and Microsoft Graph for the Entra ID Connector's scale scenario (Scenario 040), so JIM's side can be measured
# at up to a million objects without Graph's rate limits or a tenant's object limit.
#
# It serves the same MockGraphTenant the unit suite drives in process. One implementation, so a scenario cannot pass
# against a more forgiving tenant than the unit tests use.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
# Corporate networks routinely inspect outbound TLS by re-signing it with their own authority, which breaks
# certificate validation for every package restore inside the build. Supplying that authority here (base64 of a PEM
# bundle) lets the build trust it; empty (the default) is a no-op. Build-stage only: nothing from this reaches the
# final image.
ARG EXTRA_CA_CERTS_BASE64=""
RUN if [ -n "$EXTRA_CA_CERTS_BASE64" ]; then \
        echo "$EXTRA_CA_CERTS_BASE64" | base64 -d > /usr/local/share/ca-certificates/extra-build-ca.crt \
        && update-ca-certificates; \
    fi
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["Directory.Build.targets", "./"]
COPY ["VERSION", "./"]
COPY ["test/JIM.TestGraphService/JIM.TestGraphService.csproj", "test/JIM.TestGraphService/"]
COPY ["test/JIM.TestGraphService/packages.lock.json", "test/JIM.TestGraphService/"]

RUN dotnet restore "test/JIM.TestGraphService/JIM.TestGraphService.csproj"

COPY test/JIM.TestGraphService/ test/JIM.TestGraphService/

RUN dotnet publish "test/JIM.TestGraphService/JIM.TestGraphService.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

LABEL org.opencontainers.image.title="JIM Stand-in Microsoft Graph Service"
LABEL org.opencontainers.image.description="Integration-test stand-in for Microsoft Entra ID and Microsoft Graph. Not for production use."
LABEL org.opencontainers.image.vendor="Tetron"

# The certificate is written here for the scenario to read and trust.
RUN mkdir -p /certificates && chown -R app:app /certificates

USER app

ENV GRAPH_PORT=443 \
    GRAPH_HOSTNAME=graph-service \
    GRAPH_CERTIFICATE_PATH=/certificates/graph-service.pem

EXPOSE 443

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "JIM.TestGraphService.dll"]
