using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbyUserControl
{
    [Route("/EmbyUserControl/Users", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetEmbyUserControlUsers : IReturn<EmbyUserControlUserListResponse>
    {
    }

    public class EmbyUserControlUserListResponse
    {
        public List<EmbyUserControlUserInfo> Items { get; set; } = new List<EmbyUserControlUserInfo>();
    }

    public class EmbyUserControlUserInfo
    {
        public string Name { get; set; }

        public string Id { get; set; }

        public long InternalId { get; set; }

        public bool IsDisabled { get; set; }

        public bool IsHidden { get; set; }

        public bool EnableMediaPlayback { get; set; }

        public bool EnableRemoteAccess { get; set; }
    }

    public class UserListService : IService
    {
        private readonly IUserManager _userManager;

        public UserListService(IUserManager userManager)
        {
            _userManager = userManager;
        }

        public EmbyUserControlUserListResponse Get(GetEmbyUserControlUsers request)
        {
#pragma warning disable CS0618
            var users = _userManager.Users
#pragma warning restore CS0618
                .OrderBy(user => user.Name)
                .Select(user =>
                {
                    var policy = user.Policy;
                    return new EmbyUserControlUserInfo
                    {
                        Name = user.Name,
                        Id = user.Id.ToString(),
                        InternalId = user.InternalId,
                        IsDisabled = policy.IsDisabled,
                        IsHidden = policy.IsHidden,
                        EnableMediaPlayback = policy.EnableMediaPlayback,
                        EnableRemoteAccess = policy.EnableRemoteAccess
                    };
                })
                .ToList();

            return new EmbyUserControlUserListResponse
            {
                Items = users
            };
        }
    }
}
