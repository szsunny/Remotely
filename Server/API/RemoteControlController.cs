﻿using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Remotely.Server.Hubs;
using Remotely.Server.Models;
using Remotely.Server.Services;
using Remotely.Server.Auth;
using Remotely.Shared.Helpers;
using Remotely.Server.Extensions;
using Remotely.Shared.Entities;
using Remotely.Shared.Interfaces;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace Remotely.Server.API;

[Route("api/[controller]")]
[ApiController]
public class RemoteControlController : ControllerBase
{
    private readonly IHubContext<AgentHub, IAgentHubClient> _agentHub;
    private readonly IRemoteControlSessionCache _remoteControlSessionCache;
    private readonly IAgentHubSessionCache _serviceSessionCache;
    private readonly IDataService _dataService;
    private readonly IOtpProvider _otpProvider;
    private readonly SignInManager<RemotelyUser> _signInManager;
    private readonly ILogger<RemoteControlController> _logger;

    public RemoteControlController(
        SignInManager<RemotelyUser> signInManager,
        IDataService dataService,
        IRemoteControlSessionCache remoteControlSessionCache,
        IHubContext<AgentHub, IAgentHubClient> agentHub,
        IAgentHubSessionCache serviceSessionCache,
        IOtpProvider otpProvider,
        ILogger<RemoteControlController> logger)
    {
        _dataService = dataService;
        _agentHub = agentHub;
        _remoteControlSessionCache = remoteControlSessionCache;
        _serviceSessionCache = serviceSessionCache;
        _otpProvider = otpProvider;
        _signInManager = signInManager;
        _logger = logger;
    }

    [HttpGet("{deviceID}")]
    [ServiceFilter(typeof(ApiAuthorizationFilter))]
    public async Task<IActionResult> Get(string deviceID)
    {
        if (!Request.Headers.TryGetOrganizationId(out var orgId))
        {
            return Unauthorized();
        }
        
        return await InitiateRemoteControl(deviceID, orgId);
    }

    [HttpPost]
    [Obsolete("This method is deprecated. Use the GET method along with API keys instead.")]
    public async Task<IActionResult> Post([FromBody] RemoteControlRequest rcRequest)
    {
        var settings = await _dataService.GetSettings();
        if (!settings.AllowApiLogin)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(rcRequest.Email) ||
            string.IsNullOrWhiteSpace(rcRequest.Password) ||
            string.IsNullOrWhiteSpace(rcRequest.DeviceID))
        {
            return BadRequest("Request body is missing required values.");
        }

        var userResult = await _dataService.GetUserByName(rcRequest.Email);
        if (!userResult.IsSuccess)
        {
            return NotFound();
        }

        var orgId = userResult.Value.OrganizationID;

        var result = await _signInManager.PasswordSignInAsync(rcRequest.Email, rcRequest.Password, false, true);
        if (result.Succeeded &&
            _dataService.DoesUserHaveAccessToDevice(rcRequest.DeviceID, userResult.Value))
        {
            _logger.LogInformation("API login successful for {rcRequestEmail}.", rcRequest.Email);
            return await InitiateRemoteControl(rcRequest.DeviceID, orgId);
        }
        else if (result.IsLockedOut)
        {
            _logger.LogInformation("API login successful for {rcRequestEmail}.", rcRequest.Email);
            return Unauthorized("Account is locked.");
        }
        else if (result.RequiresTwoFactor)
        {
            _logger.LogInformation("API login successful for {rcRequestEmail}.", rcRequest.Email);
            return Unauthorized("Account requires two-factor authentication.");
        }
        _logger.LogInformation("API login unsuccessful due to bad attempt for {rcRequestEmail}.", rcRequest.Email);
        return BadRequest();
    }

    private async Task<IActionResult> InitiateRemoteControl(string deviceID, string orgId)
    {
        if (!_serviceSessionCache.TryGetByDeviceId(deviceID, out var targetDevice) ||
            !_serviceSessionCache.TryGetConnectionId(deviceID, out var serviceConnectionId))
        {
            return NotFound("The target device couldn't be found.");
        }

        if (targetDevice.OrganizationID != orgId)
        {
            return Unauthorized();
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            var userResult = await _dataService.GetUserByName($"{User.Identity.Name}");

            if (!userResult.IsSuccess)
            {
                return Unauthorized();
            }

            if (!_dataService.DoesUserHaveAccessToDevice(targetDevice.ID, userResult.Value))
            {
                return Unauthorized();
            }
        }

        var sessionCount = _remoteControlSessionCache.Sessions.Count(x => x.OrganizationId == orgId);

        var sessionId = Guid.NewGuid();
        var accessKey = RandomGenerator.GenerateAccessKey();

        var session = new RemoteControlSession()
        {
            UnattendedSessionId = sessionId,
            UserConnectionId = HttpContext.Connection.Id,
            AgentConnectionId = serviceConnectionId,
            DeviceId = deviceID,
            OrganizationId = orgId
        };

        _remoteControlSessionCache.AddOrUpdate($"{sessionId}", session, (k, v) =>
        {
            v.AgentConnectionId = HttpContext.Connection.Id;
            return v;
        });

        var orgNameResult = await _dataService.GetOrganizationNameById(orgId);

        if (!orgNameResult.IsSuccess)
        {
            return BadRequest("Failed to resolve organization name.");
        }

        await _agentHub.Clients.Client(serviceConnectionId).RemoteControl(
            sessionId,
            accessKey,
            HttpContext.Connection.Id,
            string.Empty,
            orgNameResult.Value,
            orgId);

        var waitResult = await session.WaitForSessionReady(TimeSpan.FromSeconds(30));
        if (!waitResult)
        {
            return StatusCode(408, "The remote control process failed to start in time on the remote device.");
        }
       
        var otp = _otpProvider.GetOtp(targetDevice.ID);

        return Ok($"{HttpContext.Request.Scheme}://{Request.Host}/Viewer?mode=Unattended&sessionId={sessionId}&accessKey={accessKey}&otp={otp}");
    }
}
