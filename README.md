# SapDocumentUploader

Descripción

SapDocumentUploader es una aplicación .NET 8 para subir documentos relacionados a SAP. Proporciona una base para integrar cargas de archivos, validación y envío hacia sistemas SAP o servicios intermedios.

Características

- Proyecto orientado a .NET 8
- Estructura preparada para extensiones: integración con SAP, validaciones y colas
- Instrucciones de compilación y despliegue local

Requisitos

- .NET SDK 8.0
- Visual Studio 2022/2024/2026 o VS Code con C# extension
- Acceso a las credenciales o endpoint de SAP (según configuración del proyecto)

Instalación y compilación

1. Clonar el repositorio:

   git clone <repo-url>

2. Abrir la solución con Visual Studio (SapDocumentUploader.slnx) o desde la línea de comandos:

   dotnet restore
   dotnet build

Ejecución

- Desde Visual Studio: abrir la solución y ejecutar (F5 o Ctrl+F5).
- Desde la CLI (proyecto principal):

  dotnet run --project ./src/NombreProyectoPrincipal/NombreProyectoPrincipal.csproj

(ajustar la ruta al proyecto principal si difiere)

Configuración

La aplicación carga opciones desde appsettings.json y variables de entorno. Recomendado:

- Configurar endpoints y credenciales SAP en appsettings.json o en las variables de entorno del sistema.
- Variables comunes (ejemplos):
  - SAP:BaseUrl
  - SAP:Username
  - SAP:Password
  - SAP:DBCompany

Ejemplo appsettings.json (resumido):

{
  "SAP": {
	"BaseUrl": "https://sap.example.local",
	"Username": "usuario",
	"Password": "secreto",
	"DBCompany": "COMPANY"
  },
  "Storage": {
	"ConnectionString": "..."
  }
}

Tests

Si el repositorio incluye proyectos de prueba, ejecutarlos con:

  dotnet test

Buenas prácticas

- No subir credenciales en texto plano al repositorio.
- Usar secretos de usuario (dotnet user-secrets) o variables de entorno en entornos de desarrollo.
- Añadir logging y manejo de errores al integrar con sistemas SAP.

Contribuir

Abrir issues y pull requests. Seguir las guías de estilo y pruebas del proyecto.

Licencia

Añado un fichero LICENSE en el repositorio o especificar la licencia preferida aquí.

Contacto

Para dudas o incidencias, abrir un issue en el repositorio.

Datos de prueba en Program.cs

Atención: el fichero Program.cs (ruta del proyecto: SAPDocumentUploader\Program.cs) contiene datos de prueba hardcodeados usados para crear documentos en SAP. Esos datos son clientes y referencias pertenecientes a la cuenta SAP del autor del proyecto. Antes de usar la aplicación en otro entorno, sustituya todos los valores hardcodeados en Program.cs por los datos de cliente y cuentas correspondientes a su propia instancia de SAP. Si no se cambian, la aplicación intentará crear documentos usando los clientes del autor y la operación fallará o creará datos en una cuenta ajena.

Recomendaciones:
- Revise y actualice: números de cliente, centros, tipos de documento y cualquier referencia específica de la cuenta.
- No deje credenciales ni datos sensibles en el repositorio; use appsettings.json, variables de entorno o dotnet user-secrets.
- Verifique los cambios en un entorno de pruebas de su SAP antes de operar en producción.
