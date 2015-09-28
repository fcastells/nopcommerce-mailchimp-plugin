using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Nop.Core.Domain.Customers;
using Nop.Plugin.Misc.MailChimp.Data;
using Nop.Services.Common;
using Nop.Services.Customers;
using Nop.Services.Logging;

/*old library*/
//using PerceptiveMCAPI;
//using PerceptiveMCAPI.Methods;
//using PerceptiveMCAPI.Types;

using MailChimp;
using MailChimp.Helper;
using MailChimp.Lists;


namespace Nop.Plugin.Misc.MailChimp.Services
{
    public class MailChimpApiService : IMailChimpApiService
    {
        private readonly MailChimpSettings _mailChimpSettings;
        private readonly ISubscriptionEventQueueingService _subscriptionEventQueueingService;
        private readonly ICustomerService _customerService;
        private readonly ILogger _log;

        public MailChimpApiService(MailChimpSettings mailChimpSettings, 
            ISubscriptionEventQueueingService subscriptionEventQueueingService, 
            ICustomerService customerService,
            ILogger log)
        {
            _mailChimpSettings = mailChimpSettings;
            _subscriptionEventQueueingService = subscriptionEventQueueingService;
            _customerService = customerService;
            _log = log;
        }

        /// <summary>
        /// Retrieves the lists.
        /// </summary>
        /// <returns></returns>
        public virtual NameValueCollection RetrieveLists()
        {
            var output = new NameValueCollection();
            try
            {
                var mc = new MailChimpManager(_mailChimpSettings.ApiKey);
                ListResult lists = mc.GetLists();
                if (lists != null && lists.Data != null)
                {
                    lists.Data.ForEach(l=>output.Add(l.Name, l.Id));
                }
            }
            catch (Exception e)
            {
                _log.Debug(e.Message, e);
            }
            return output;
        }

        /// <summary>
        /// Batches the unsubscribe.
        /// </summary>
        /// <param name="recordList">The records</param>
        public virtual BatchUnsubscribeResult BatchUnsubscribe(IEnumerable<MailChimpEventQueueRecord> recordList)
        {
            if (String.IsNullOrEmpty(_mailChimpSettings.DefaultListId)) 
                throw new ArgumentException("MailChimp list is not specified");

            var mc = new MailChimpManager(_mailChimpSettings.ApiKey);
          
            var emails = recordList.Select(sub => new EmailParameter() {Email = sub.Email}).ToList();

            MemberInfoResult emailInfos = mc.GetMemberInfo(_mailChimpSettings.DefaultListId, emails);

            emails.Clear();

            foreach (var member in emailInfos.Data)
            {
                if (member!=null && member.Status.ToLower().Contains("subscribed")) 
                {
                    emails.Add(new EmailParameter() { Email = member.Email });
                }
            }
           
            ////remove email if it's subscribed to mailchimp list
            BatchUnsubscribeResult results = mc.BatchUnsubscribe(_mailChimpSettings.DefaultListId, emails, false, true, true);
            
            return results;
        }


        /// <summary>
        /// Batches the subscribe.
        /// </summary>
        /// <param name="recordList">The records</param>
        public virtual BatchSubscribeResult BatchSubscribe(IEnumerable<MailChimpEventQueueRecord> recordList)
        {
            if (string.IsNullOrEmpty(_mailChimpSettings.DefaultListId)) 
                throw new ArgumentException("MailChimp list is not specified");

            var mc = new MailChimpManager(_mailChimpSettings.ApiKey);
            var batchEmailParam = new List<BatchEmailParameter>();

            foreach (var sub in recordList)
            {
                try
                {
                    var emailParam = new EmailParameter
                    {
                        Email = sub.Email
                    };

                    var mergeVars = new MergeVar();

                    // TODO Customize your merge vars

                    // get customer and attributes
                    var customer = _customerService.GetCustomerByEmail(sub.Email);
                    if (customer != null)
                    {
                        AddAttribute(mergeVars, customer, SystemCustomerAttributeNames.FirstName, "FNAME");
                        AddAttribute(mergeVars, customer, SystemCustomerAttributeNames.LastName, "LNAME");
                        AddAttribute(mergeVars, customer, SystemCustomerAttributeNames.Phone, "PHONE");

                        var gender = customer.GetAttribute<string>(SystemCustomerAttributeNames.Gender);
                        switch (gender)
                        {
                            case "F":
                                mergeVars.Add("GENDER", "Mujer");
                                break;
                            case "M":
                                mergeVars.Add("GENDER", "Hombre");
                                break;
                            default:
                                mergeVars.Add("GENDER", "No especificado");
                                break;
                        }
                    }
                    
                    //add to group
                    mergeVars.Groupings = new List<Grouping>() {new Grouping()};
                    mergeVars.Groupings[0].Name = "Yo soy";
                    mergeVars.Groupings[0].GroupNames = new List<string> { "Deportista" };

                    batchEmailParam.Add(new BatchEmailParameter()
                    {
                        Email = emailParam,
                        MergeVars = mergeVars
                    });
                }
                catch (Exception ex)
                {
                    _log.Warning(string.Format("Could not register email {0} to Mailchimp", sub.Email), ex);
                }
            }

            BatchSubscribeResult results = mc.BatchSubscribe(_mailChimpSettings.DefaultListId, batchEmailParam, true, true, false);
            return results;
        }

        private static void AddAttribute(MergeVar myMergeVars, Customer customer, string attribute, string mailchimpField)
        {
            var value = customer.GetAttribute<string>(attribute);
            myMergeVars.Add(mailchimpField, value);
        }

        public virtual SyncResult Synchronize()
        {
            var result = new SyncResult();

            // Get all the queued records for subscription/unsubscription
            var allRecords = _subscriptionEventQueueingService.GetAll();
            //get unique and latest records
            var allRecordsUnique = new List<MailChimpEventQueueRecord>();
            foreach (var item in allRecords.OrderByDescending(x => x.CreatedOnUtc))
            {
                var exists = allRecordsUnique.FirstOrDefault(x => x.Email.Equals(item.Email, StringComparison.InvariantCultureIgnoreCase)) != null;
                if (!exists)
                    allRecordsUnique.Add(item);
            }
            var subscribeRecords = allRecordsUnique.Where(x => x.IsSubscribe).ToList();
            var unsubscribeRecords = allRecordsUnique.Where(x => !x.IsSubscribe).ToList();
            
            //subscribe
            if (subscribeRecords.Any())
            {
                BatchSubscribeResult subscribeResult = BatchSubscribe(subscribeRecords);
                if (subscribeResult.AddCount > 0)
                {
                    result.SubscribeResult = string.Format("Added {0} new records", subscribeResult.AddCount);
                }
                if(subscribeResult.UpdateCount>0){
                    result.SubscribeResult = string.Format("Updated {0} new records", subscribeResult.UpdateCount);
                }
            }
            else
            {
                result.SubscribeResult = "No records to add";
            }

            //unsubscribe
            if (unsubscribeRecords.Any())
            {
                var unsubscribeResultCount = BatchUnsubscribe(unsubscribeRecords);
                if (unsubscribeResultCount.SuccessCount > 0)
                {
                    result.UnsubscribeResult = string.Format("Unsubscribe {0} complete ", unsubscribeResultCount);
                }
            }
            else
            {
                result.UnsubscribeResult = "No records to unsubscribe";
            }

            //delete the queued records
            foreach (var sub in allRecords)
            {
                _subscriptionEventQueueingService.Delete(sub);
            }

            return result;
        }
    }
}