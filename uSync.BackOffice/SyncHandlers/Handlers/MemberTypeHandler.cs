﻿using Microsoft.Extensions.Logging;

using System;

using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

using uSync.BackOffice.Configuration;
using uSync.BackOffice.Services;
using uSync.Core;

using static Umbraco.Cms.Core.Constants;

namespace uSync.BackOffice.SyncHandlers.Handlers
{
    [SyncHandler(uSyncConstants.Handlers.MemberTypeHandler, "Member Types", "MemberTypes", uSyncConstants.Priorites.MemberTypes,
        IsTwoPass = true, Icon = "icon-users", EntityType = UdiEntityType.MemberType)]
    public class MemberTypeHandler : SyncHandlerContainerBase<IMemberType, IMemberTypeService>, ISyncHandler,
        INotificationHandler<SavedNotification<IMemberType>>,
        INotificationHandler<MovedNotification<IMemberType>>,
        INotificationHandler<DeletedNotification<IMemberType>>
    {
        private readonly IMemberTypeService memberTypeService;

        public MemberTypeHandler(
            ILogger<MemberTypeHandler> logger,
            IEntityService entityService,
            IMemberTypeService memberTypeService,
            AppCaches appCaches,
            IShortStringHelper shortStringHelper,
            SyncFileService syncFileService,
            uSyncEventService mutexService,
            uSyncConfigService uSyncConfig,
            ISyncItemFactory syncItemFactory)
            : base(logger, entityService, appCaches, shortStringHelper, syncFileService, mutexService, uSyncConfig, syncItemFactory)
        {
            this.memberTypeService = memberTypeService;
        }
        protected override void DeleteFolder(int id)
            => memberTypeService.DeleteContainer(id);

        protected override IEntity GetContainer(int id)
            => memberTypeService.GetContainer(id);

        protected override IEntity GetContainer(Guid key)
            => memberTypeService.GetContainer(key);

        protected override string GetEntityTreeName(IUmbracoEntity item, bool useGuid)
        {
            if (useGuid) return item.Key.ToString();

            if (item is IMemberType memberType)
            {
                return memberType.Alias.ToSafeFileName(shortStringHelper);
            }

            return item.Name.ToSafeFileName(shortStringHelper);
        }
    }

}
