﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;

using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

using uSync.Core.Models;

namespace uSync.Core.Serialization.Serializers
{
    [SyncSerializer("B3F7F247-6077-406D-8480-DB1004C8211C", "ContentTypeSerializer", uSyncConstants.Serialization.ContentType)]
    public class ContentTypeSerializer : ContentTypeBaseSerializer<IContentType>, ISyncSerializer<IContentType>
    {
        private readonly IContentTypeService contentTypeService;
        private readonly IFileService fileService;

        public ContentTypeSerializer(
            IEntityService entityService, ILogger<ContentTypeSerializer> logger,
            IDataTypeService dataTypeService,
            IContentTypeService contentTypeService,
            IFileService fileService,
            IShortStringHelper shortStringHelper)
            : base(entityService, logger, dataTypeService, contentTypeService, UmbracoObjectTypes.DocumentTypeContainer, shortStringHelper)
        {
            this.contentTypeService = contentTypeService;
            this.fileService = fileService;
        }

        protected override SyncAttempt<XElement> SerializeCore(IContentType item, SyncSerializerOptions options)
        {
            var node = SerializeBase(item);
            var info = SerializeInfo(item);

            var parent = item.ContentTypeComposition.FirstOrDefault(x => x.Id == item.ParentId);
            if (parent != null)
            {
                info.Add(new XElement("Parent", parent.Alias,
                            new XAttribute("Key", parent.Key)));
            }
            else if (item.Level != 1)
            {
                var folderNode = this.GetFolderNode(item);
                if (folderNode != null)
                    info.Add(folderNode);
            }

            // compositions ? 
            info.Add(SerializeCompostions((ContentTypeCompositionBase)item));

            // templates
            var templateAlias =
                (item.DefaultTemplate != null && item.DefaultTemplate.Id != 0)
                ? item.DefaultTemplate.Alias
                : "";

            info.Add(new XElement("DefaultTemplate", templateAlias));

            var templates = SerailizeTemplates(item);
            if (templates != null)
                info.Add(templates);

            node.Add(info);
            node.Add(SerializeStructure(item));
            node.Add(SerializeProperties(item));
            node.Add(SerializeTabs(item));

            return SyncAttempt<XElement>.Succeed(item.Name, node, typeof(IContentType), ChangeType.Export);
        }

        protected override void SerializeExtraProperties(XElement node, IContentType item, IPropertyType property)
        {
            node.Add(new XElement("Variations", property.Variations));
        }

        private XElement SerailizeTemplates(IContentType item)
        {
            var node = new XElement("AllowedTemplates");
            if (item.AllowedTemplates.Any())
            {
                foreach (var template in item.AllowedTemplates.OrderBy(x => x.Alias))
                {
                    node.Add(new XElement("Template", template.Alias,
                        new XAttribute("Key", template.Key)));
                }
            }

            return node;
        }

        protected override SyncAttempt<IContentType> DeserializeCore(XElement node, SyncSerializerOptions options)
        {
            var attempt = FindOrCreate(node);
            if (!attempt.Success) throw attempt.Exception;

            var item = attempt.Result;

            var details = new List<uSyncChange>();

            details.AddRange(DeserializeBase(item, node));
            details.AddRange(DeserializeTabs(item, node));
            details.AddRange(DeserializeProperties(item, node, options));

            // content type only property stuff.
            details.AddRange(DeserializeContentTypeProperties(item, node));

            // templates 
            details.AddRange(DeserializeTemplates(item, node));

            // contentTypeService.Save(item);

            return SyncAttempt<IContentType>.Succeed(item.Name, item, ChangeType.Import, details);
        }

        protected override IEnumerable<uSyncChange> DeserializeExtraProperties(IContentType item, IPropertyType property, XElement node)
        {
            var variations = node.Element("Variations").ValueOrDefault(ContentVariation.Nothing);
            if (property.Variations != variations)
            {
                var change = uSyncChange.Update("Property/Variations", "Variations", property.Variations, variations);

                property.Variations = variations;

                return change.AsEnumerableOfOne();
            }

            return Enumerable.Empty<uSyncChange>();
        }

        public override SyncAttempt<IContentType> DeserializeSecondPass(IContentType item, XElement node, SyncSerializerOptions options)
        {
            logger.LogDebug("Deserialize Second Pass {0}", item.Alias);

            var details = new List<uSyncChange>();

            details.AddRange(DeserializeCompositions(item, node));
            details.AddRange(DeserializeStructure(item, node));

            CleanTabAliases(item);

            // clean tabs 
            details.AddRange(CleanTabs(item, node, options));

            bool saveInSerializer = !options.Flags.HasFlag(SerializerFlags.DoNotSave);
            if (saveInSerializer && item.IsDirty())
            {
                var dirty = string.Join(", ", item.GetDirtyProperties());
                dirty += string.Join(", ", item.PropertyGroups.Where(x => x.IsDirty()).Select(x => $"Group:{x.Name}"));
                dirty += string.Join(", ", item.PropertyTypes.Where(x => x.IsDirty()).Select(x => $"Property:{x.Name}"));
                logger.LogDebug("Saving in Serializer because item is dirty [{properties}]", dirty);

                contentTypeService.Save(item);
            }

            CleanFolder(item, node);

            return SyncAttempt<IContentType>.Succeed(item.Name, item, ChangeType.Import, "", saveInSerializer, details);
        }

        private IEnumerable<uSyncChange> DeserializeContentTypeProperties(IContentType item, XElement node)
        {
            var info = node?.Element("Info");
            if (info == null) return Enumerable.Empty<uSyncChange>();

            var changes = new List<uSyncChange>();

            var isContainer = info.Element("IsListView").ValueOrDefault(false);
            if (item.IsContainer != isContainer)
            {
                changes.AddUpdate("IsListView", item.IsContainer, isContainer, "Info/IsListView");
                item.IsContainer = isContainer;
            }

            var masterTemplate = info.Element("DefaultTemplate").ValueOrDefault(string.Empty);
            if (!string.IsNullOrEmpty(masterTemplate))
            {
                var template = fileService.GetTemplate(masterTemplate);
                if (template != null)
                {
                    if (item.DefaultTemplate == null || template.Alias != item.DefaultTemplate.Alias)
                    {
                        changes.AddUpdate("DefaultTemplate", item.DefaultTemplate?.Alias ?? string.Empty, masterTemplate, "DefaultTemplate");
                        item.SetDefaultTemplate(template);
                    }
                }
                else
                {
                    // elements don't have a defaultTemplate, but it can be valid to have the old defaultTemplate in the db.
                    // (it would then re-appear if the user untoggles is element) See issue #203
                    //
                    // So we only log this as a problem if the default template is missing on a non-element doctype. 
                    if (!item.IsElement)
                    {

                        changes.AddUpdate("DefaultTemplate", item.DefaultTemplate?.Alias ?? string.Empty, "Cannot find Template", "DefaultTemplate", false);
                    }
                }
            }

            return changes;
        }

        private IEnumerable<uSyncChange> DeserializeTemplates(IContentType item, XElement node)
        {
            var templates = node?.Element("Info")?.Element("AllowedTemplates");
            if (templates == null) return Enumerable.Empty<uSyncChange>();

            var allowedTemplates = new List<ITemplate>();
            var changes = new List<uSyncChange>();


            foreach (var template in templates.Elements("Template"))
            {
                var alias = template.Value;
                var key = template.Attribute("Key").ValueOrDefault(Guid.Empty);

                var templateItem = default(ITemplate);

                if (key != Guid.Empty)
                    templateItem = fileService.GetTemplate(key);

                if (templateItem == null)
                    templateItem = fileService.GetTemplate(alias);

                if (templateItem != null)
                {
                    logger.LogDebug("Adding Template: {0}", templateItem.Alias);
                    allowedTemplates.Add(templateItem);
                }
            }


            var currentTemplates = string.Join(",", item.AllowedTemplates.Select(x => x.Alias).OrderBy(x => x));
            var newTemplates = string.Join(",", allowedTemplates.Select(x => x.Alias).OrderBy(x => x));

            if (currentTemplates != newTemplates)
            {
                changes.AddUpdate("AllowedTemplates", currentTemplates, newTemplates, "AllowedTemplates");
            }

            item.AllowedTemplates = allowedTemplates;

            return changes;
        }


        protected override Attempt<IContentType> CreateItem(string alias, ITreeEntity parent, string itemType)
        {
            var item = new ContentType(shortStringHelper, -1)
            {
                Alias = alias
            };

            if (parent != null)
            {
                if (parent is IContentType parentContent)
                {
                    item.AddContentType(parentContent);
                }

                item.SetParent(parent);
            }

            return Attempt.Succeed((IContentType)item);
        }

        protected override void SaveContainer(EntityContainer container)
        {
            logger.LogDebug("Saving Container (In main class) {0}", container.Key.ToString());
            contentTypeService.SaveContainer(container);
        }
    }
}
