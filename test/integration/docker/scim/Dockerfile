# syntax=docker/dockerfile:1
# jim-compliance: test-image
# Test infrastructure only; this image is never shipped to customers. It exists so the SCIM Connector
# can be driven end to end against a service provider JIM reaches the way it reaches a real one: over
# HTTPS, by hostname, with a certificate to validate.
#
# The provider it serves is the same MockScimProvider the unit suite drives in process. One
# implementation, so an integration run cannot pass against a more forgiving provider than the unit
# tests use.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
# Corporate networks routinely inspect outbound TLS by re-signing it with their own authority, which
# breaks certificate validation for every package restore inside the build. Supplying that authority
# here (base64 of a PEM bundle) lets the build trust it; empty (the default) is a no-op. Build-stage
# only: nothing from this reaches the final image.
ARG EXTRA_CA_CERTS_BASE64=""
RUN if [ -n "$EXTRA_CA_CERTS_BASE64" ]; then \
        echo "$EXTRA_CA_CERTS_BASE64" | base64 -d > /usr/local/share/ca-certificates/extra-build-ca.crt \
        && update-ca-certificates; \
    fi
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["VERSION", "./"]
COPY ["test/JIM.TestScimServiceProvider/JIM.TestScimServiceProvider.csproj", "test/JIM.TestScimServiceProvider/"]
COPY ["test/JIM.TestScimServiceProvider/packages.lock.json", "test/JIM.TestScimServiceProvider/"]
COPY ["src/JIM.Scim/JIM.Scim.csproj", "src/JIM.Scim/"]
COPY ["src/JIM.Scim/packages.lock.json", "src/JIM.Scim/"]
COPY ["src/JIM.Models/JIM.Models.csproj", "src/JIM.Models/"]
COPY ["src/JIM.Models/packages.lock.json", "src/JIM.Models/"]

RUN dotnet restore "test/JIM.TestScimServiceProvider/JIM.TestScimServiceProvider.csproj"

COPY src/ src/
COPY test/JIM.TestScimServiceProvider/ test/JIM.TestScimServiceProvider/

RUN dotnet publish "test/JIM.TestScimServiceProvider/JIM.TestScimServiceProvider.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

LABEL org.opencontainers.image.title="JIM SCIM 2.0 Test Service Provider"
LABEL org.opencontainers.image.description="Integration-test SCIM 2.0 service provider. Not for production use."
LABEL org.opencontainers.image.vendor="Tetron"

# The certificate is written here for the scenario to read and trust. A named volume shares it with the
# host without the container needing to be privileged.
RUN mkdir -p /certificates && chown -R app:app /certificates

USER app

ENV SCIM_PORT=5300 \
    SCIM_HOSTNAME=scim-provider \
    SCIM_CERTIFICATE_PATH=/certificates/scim-provider.pem

EXPOSE 5300

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "JIM.TestScimServiceProvider.dll"]
