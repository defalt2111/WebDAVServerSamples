Imports System
Imports System.Collections.Generic
Imports System.Configuration
Imports System.IO
Imports System.Web
Imports System.Xml
Imports System.Xml.Serialization
Imports System.Threading.Tasks
Imports ITHit.Server
Imports ITHit.WebDAV.Server
Imports ITHit.GSuite.Server

''' <summary>
''' This handler processes WebDAV requests.
''' </summary>
Public Class DavHandler
    Inherits HttpTaskAsyncHandler

    ''' <summary>
    ''' This license file is used to activate:
    '''  - IT Hit WebDAV Server Engine for .NET
    '''  - IT Hit iCalendar and vCard Library if used in a project
    ''' </summary>
    Private ReadOnly license As String = File.ReadAllText(HttpContext.Current.Request.PhysicalApplicationPath & "License.lic")

    ''' <summary>
    ''' Google Service Account ID (client_email field from JSON file).
    ''' </summary>
    Private Shared ReadOnly googleServiceAccountID As String = ConfigurationManager.AppSettings("GoogleServiceAccountID")

    ''' <summary>
    ''' Google Service private key (private_key field from JSON file).
    ''' </summary>
    Private Shared ReadOnly googleServicePrivateKey As String = ConfigurationManager.AppSettings("GoogleServicePrivateKey")

    ''' <summary>
    ''' Relative Url of "Webhook" callback. It handles the API notification messages that are triggered when a resource changes.
    ''' </summary>
    Private Shared ReadOnly googleNotificationsRelativeUrl As String = ConfigurationManager.AppSettings("GoogleNotificationsRelativeUrl")

    ''' <summary>
    ''' This license file is used to activate G Suite Documents Editing for IT Hit WebDAV Server
    ''' </summary>
    Private ReadOnly gSuiteLicense As String = If(File.Exists(HttpContext.Current.Request.PhysicalApplicationPath & "GSuiteLicense.lic"), File.ReadAllText(HttpContext.Current.Request.PhysicalApplicationPath & "GSuiteLicense.lic"), String.Empty)

    ''' <summary>
    ''' If debug logging is enabled reponses are output as formatted XML,
    ''' all requests and response headers and most bodies are logged.
    ''' If debug logging is disabled only errors are logged.
    ''' </summary>
    Private Shared ReadOnly debugLoggingEnabled As Boolean = "true".Equals(ConfigurationManager.AppSettings("DebugLoggingEnabled"),
                                                                          StringComparison.InvariantCultureIgnoreCase)

    ''' <summary>
    ''' Gets a value indicating whether another request can use the
    ''' <see cref="T:System.Web.IHttpHandler"/>  instance.
    ''' </summary>
    ''' <returns>
    ''' true if the <see cref="T:System.Web.IHttpHandler"/>  instance is reusable; otherwise, false.
    ''' </returns>
    Public Overrides ReadOnly Property IsReusable As Boolean
        Get
            Return True
        End Get
    End Property

    ''' <summary>
    ''' Enables processing of HTTP Web requests.
    ''' </summary>
    ''' <param name="context">An <see cref="T:System.Web.HttpContext"/>  object that provides references to the
    ''' intrinsic server objects (for example, Request, Response, Session, and Server) used to service
    ''' HTTP requests. 
    ''' </param>
    Public Overrides Async Function ProcessRequestAsync(context As HttpContext) As Task
        Dim webDavEngine As DavEngineAsync = getOrInitializeWebDavEngine(context)
        context.Response.BufferOutput = False
        Dim gSuiteEngine As GSuiteEngineAsync = getOrInitializeGSuiteEngine(context)
        Using sqlDavContext = New DavContext(context)
            Await webDavEngine.RunAsync(sqlDavContext)
            If gSuiteEngine IsNot Nothing Then
                Await gSuiteEngine.RunAsync(ContextConverter.ConvertToGSuiteContext(sqlDavContext))
            End If
        End Using
    End Function

    ''' <summary>
    ''' Initializes engine.
    ''' </summary>
    ''' <param name="context">Instance of <see cref="HttpContext"/> .</param>
    ''' <returns>Initialized <see cref="DavEngine"/> .</returns>
    Private Function initializeWebDavEngine(context As HttpContext) As DavEngineAsync
        Dim logger As ILogger = WebDAVServer.SqlStorage.AspNet.Logger.Instance
        Dim webDavEngine As DavEngineAsync = New DavEngineAsync With {.Logger = logger,
                                                                .OutputXmlFormatting = True}
        webDavEngine.License = license
        Dim contentRootPath As String = HttpContext.Current.Request.MapPath("/")
        ' Set custom handler to process GET and HEAD requests to folders and display 
        ' info about how to connect to server. We are using the same custom handler 
        ' class (but different instances) here to process both GET and HEAD because 
        ' these requests are very similar. Some WebDAV clients may fail to connect if HEAD 
        ' request is not processed.
        Dim handlerGet As MyCustomGetHandler = New MyCustomGetHandler(contentRootPath)
        Dim handlerHead As MyCustomGetHandler = New MyCustomGetHandler(contentRootPath)
        handlerGet.OriginalHandler = webDavEngine.RegisterMethodHandler("GET", handlerGet)
        handlerHead.OriginalHandler = webDavEngine.RegisterMethodHandler("HEAD", handlerHead)
        Return webDavEngine
    End Function

    ''' <summary>
    ''' Initializes or gets engine singleton.
    ''' </summary>
    ''' <param name="context">Instance of <see cref="HttpContext"/> .</param>
    ''' <returns>Instance of <see cref="DavEngineAsync"/> .</returns>
    Private Function getOrInitializeWebDavEngine(context As HttpContext) As DavEngineAsync
        'we don't use any double check lock pattern here because nothing wrong
        'is going to happen if we created occasionally several engines.
        Const ENGINE_KEY As String = "$DavEngine$"
        If context.Application(ENGINE_KEY) Is Nothing Then
            context.Application(ENGINE_KEY) = initializeWebDavEngine(context)
        End If

        Return CType(context.Application(ENGINE_KEY), DavEngineAsync)
    End Function

    ''' <summary>
    ''' Initializes or gets engine singleton.
    ''' </summary>
    ''' <param name="context">Instance of <see cref="HttpContext"/> .</param>
    ''' <returns>Instance of <see cref="GSuiteEngineAsync"/> .</returns>
    Private Function getOrInitializeGSuiteEngine(context As HttpContext) As GSuiteEngineAsync
        'we don't use any double check lock pattern here because nothing wrong
        'is going to happen if we created occasionally several engines.
        Const ENGINE_KEY As String = "$GSuiteEngine$"
        If String.IsNullOrEmpty(googleServiceAccountID) OrElse String.IsNullOrEmpty(googleServicePrivateKey) Then
            Return Nothing
        End If

        If context.Application(ENGINE_KEY) Is Nothing Then
            Dim gSuiteEngine = New GSuiteEngineAsync(googleServiceAccountID, googleServicePrivateKey, googleNotificationsRelativeUrl) With {.License = gSuiteLicense, 
                                                                                                                                      .Logger = WebDAVServer.SqlStorage.AspNet.Logger.Instance
                                                                                                                                      }
            context.Application(ENGINE_KEY) = gSuiteEngine
        End If

        Return CType(context.Application(ENGINE_KEY), GSuiteEngineAsync)
    End Function
End Class
