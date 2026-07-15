using System;

namespace ERP_RFQ_Automation.Models
{
    public class ServiceResult<T>
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public T Data { get; set; }
        public string ErrorCode { get; set; }

        public static ServiceResult<T> CreateSuccess(T data, string message = "Success")
        {
            return new ServiceResult<T>
            {
                Success = true,
                Data = data,
                Message = message
            };
        }

        public static ServiceResult<T> CreateFailure(string message, string errorCode = null)
        {
            return new ServiceResult<T>
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode,
                Data = default
            };
        }
    }
}
